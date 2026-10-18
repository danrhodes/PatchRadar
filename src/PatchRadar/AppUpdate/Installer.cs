using System.IO;
using System.Text.Json;
using PatchRadar.Infra;

namespace PatchRadar.AppUpdate;

/// <summary>File operations the installer needs, as a seam so failures (locked file, AV block) can be injected in tests.</summary>
public interface IInstallFileOps
{
    bool Exists(string path);
    void Move(string from, string to);
    void Copy(string from, string to);
    void Delete(string path);
    bool IsDirectoryWritable(string dir);
}

public sealed class InstallFileOps : IInstallFileOps
{
    public bool Exists(string path) => File.Exists(path);
    public void Move(string from, string to) => File.Move(from, to);
    public void Copy(string from, string to) => File.Copy(from, to, overwrite: false);
    public void Delete(string path) => File.Delete(path);

    public bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".patchradar-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
    }
}

/// <summary>Written before the new exe starts; read by every start until the new build proves healthy.</summary>
public sealed class PendingUpdate
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Attempts { get; set; }
    public bool Healthy { get; set; }
}

public sealed class PendingStore(string updatesDir, ILog? log = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;
    public string PendingPath => Path.Combine(updatesDir, "pending.json");
    private string NoticePath => Path.Combine(updatesDir, "notice.json");

    public PendingUpdate? Load()
    {
        try
        {
            return File.Exists(PendingPath) ? JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(PendingPath)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _log.Warn("pending.json unreadable: " + ex.Message);
            return null;
        }
    }

    public void Save(PendingUpdate p)
    {
        Directory.CreateDirectory(updatesDir);
        var tmp = PendingPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(p));
        File.Move(tmp, PendingPath, overwrite: true);
    }

    public void Delete()
    {
        try { File.Delete(PendingPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Could not delete pending.json: " + ex.Message); }
    }

    /// <summary>A one-shot message for the next start (for example "rolled back").</summary>
    public void SetNotice(string message)
    {
        try { Directory.CreateDirectory(updatesDir); File.WriteAllText(NoticePath, JsonSerializer.Serialize(new { message })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.Warn("Could not write notice.json: " + ex.Message); }
    }

    public string? TakeNotice()
    {
        try
        {
            if (!File.Exists(NoticePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(NoticePath));
            File.Delete(NoticePath);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
}

public enum ApplyResult { Applied, NotWritable, Failed }

/// <summary>
/// In-process swap, no helper and no elevation: rename the running exe to .old (Windows allows that),
/// copy the verified exe into place, record pending.json. If anything fails the original is put back,
/// so the install never ends up without a working exe.
/// </summary>
public sealed class UpdateInstaller(IInstallFileOps ops, PendingStore pending, string installedExePath, ILog? log = null)
{
    private readonly ILog _log = log ?? NullLog.Instance;
    public string InstalledPath => installedExePath;
    public string OldPath => installedExePath + ".old";
    public string FailedPath => installedExePath + ".failed";

    public bool CanWrite => ops.IsDirectoryWritable(Path.GetDirectoryName(installedExePath)!);

    public ApplyResult Apply(string verifiedExe, AppVersion from, AppVersion to)
    {
        if (!CanWrite)
        {
            _log.Warn($"Install directory is not writable ({Path.GetDirectoryName(installedExePath)}); falling back to notify-only for {to}");
            return ApplyResult.NotWritable;
        }

        try
        {
            if (ops.Exists(OldPath)) ops.Delete(OldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Update aborted: cannot clear the previous .old file", ex);
            return ApplyResult.Failed;
        }

        try { ops.Move(installedExePath, OldPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Update aborted: could not rename the running exe (antivirus or permissions?)", ex);
            return ApplyResult.Failed;
        }

        try { ops.Copy(verifiedExe, installedExePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Update aborted: could not place the new exe; restoring the current one", ex);
            Restore();
            return ApplyResult.Failed;
        }

        try { pending.Save(new PendingUpdate { From = from.ToString(), To = to.ToString(), Attempts = 0, Healthy = false }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Update aborted: could not write pending.json; restoring the current exe", ex);
            try { ops.Delete(installedExePath); } catch { }
            Restore();
            return ApplyResult.Failed;
        }

        _log.Info($"Update {from} -> {to} staged in place; restart required");
        return ApplyResult.Applied;
    }

    private bool Restore()
    {
        try
        {
            if (ops.Exists(installedExePath)) ops.Delete(installedExePath);
            ops.Move(OldPath, installedExePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Restore by rename failed (" + ex.Message + "); trying a copy");
        }

        // A blocked rename does not always block a copy (an AV scanner may hold the file open for reading only).
        try
        {
            if (ops.Exists(installedExePath)) ops.Delete(installedExePath);
            ops.Copy(OldPath, installedExePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error($"CRITICAL: could not restore {installedExePath} from .old; the previous copy is at {OldPath}", ex);
            return false;
        }
    }

    /// <summary>Roll the install back to the .old copy. The running (failing) exe is moved aside, not deleted.</summary>
    public bool Rollback()
    {
        try
        {
            if (!ops.Exists(OldPath)) { _log.Error("Rollback impossible: no .old copy"); return false; }
            if (ops.Exists(FailedPath)) ops.Delete(FailedPath);
            ops.Move(installedExePath, FailedPath);
            try { ops.Move(OldPath, installedExePath); }
            catch
            {
                ops.Move(FailedPath, installedExePath);   // put the failing build back rather than leave nothing
                throw;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("Rollback failed", ex);
            return false;
        }
    }

    /// <summary>Best-effort removal of leftovers from a finished update.</summary>
    public void CleanLeftovers()
    {
        foreach (var p in new[] { OldPath, FailedPath })
        {
            try { if (ops.Exists(p)) ops.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

public enum StartupAction { Normal, JustUpdated, RolledBack, RollbackFailed }

public sealed record StartupOutcome(StartupAction Action, string? Version = null);

/// <summary>
/// Health rule: every start with pending.json increments attempts. A start that finds attempts &gt;= 2 and
/// healthy == false means the new build failed twice without surviving 60 s: roll back.
/// </summary>
public sealed class UpdateHealth(PendingStore pending, UpdateInstaller installer, ILog? log = null)
{
    public static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(60);
    private readonly ILog _log = log ?? NullLog.Instance;

    /// <param name="setSkipped">Called with the failed version when a rollback happens.</param>
    public StartupOutcome OnStartup(AppVersion running, Action<string> setSkipped)
    {
        var p = pending.Load();
        if (p is null)
        {
            installer.CleanLeftovers();      // stale .old/.failed from an update that finished long ago
            return new StartupOutcome(StartupAction.Normal);
        }

        if (!AppVersion.TryParse(p.To, out var target) || !target.Equals(running))
        {
            // Not the build pending.json was written for (manual replace, or already rolled back).
            _log.Info($"Ignoring stale pending.json (to {p.To}, running {running})");
            pending.Delete();
            installer.CleanLeftovers();
            return new StartupOutcome(StartupAction.Normal);
        }

        if (!p.Healthy && p.Attempts >= 2)
        {
            _log.Error($"Update to {p.To} failed {p.Attempts} starts in a row; rolling back to {p.From}");
            if (installer.Rollback())
            {
                setSkipped(p.To);
                pending.Delete();
                pending.SetNotice($"Update v{p.To} failed, rolled back to v{p.From}");
                return new StartupOutcome(StartupAction.RolledBack, p.From);
            }
            pending.Delete();      // do not loop forever on a rollback that cannot work
            return new StartupOutcome(StartupAction.RollbackFailed, p.To);
        }

        p.Attempts++;
        pending.Save(p);
        _log.Info($"Start {p.Attempts} of update {p.From} -> {p.To}");
        return new StartupOutcome(p.Attempts == 1 ? StartupAction.JustUpdated : StartupAction.Normal, p.To);
    }

    /// <summary>The instance has run long enough: the update is good. Remove .old and pending.json.</summary>
    public void MarkHealthy()
    {
        var p = pending.Load();
        if (p is null) return;
        p.Healthy = true;
        try { pending.Save(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        installer.CleanLeftovers();
        pending.Delete();
        _log.Info($"Update to {p.To} is healthy");
    }
}
