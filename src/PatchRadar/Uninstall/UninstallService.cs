using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using PatchRadar.AppUpdate;
using PatchRadar.Core;
using PatchRadar.Infra;
using PatchRadar.Settings;

namespace PatchRadar.Uninstall;

// ---- who is using the app -----------------------------------------------------------------------------

public interface IPrivilegeInfo
{
    /// <summary>True for administrators, whether or not this process is elevated (UAC-limited admins count).</summary>
    bool IsAdminCapable { get; }
    /// <summary>DOMAIN\user of the person running the app, for the audit record.</summary>
    string UserName { get; }
    string MachineName { get; }
}

public sealed class WindowsPrivilegeInfo : IPrivilegeInfo
{
    private const int TokenElevationType = 18;
    private const int ElevationLimited = 3;     // an administrator running with the filtered UAC token

    public string UserName => WindowsIdentity.GetCurrent().Name;
    public string MachineName => Environment.MachineName;

    public bool IsAdminCapable
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return Classify(new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator), ElevationType(id));
        }
    }

    /// <summary>
    /// Elevated admin: IsInRole is true. UAC-limited admin: IsInRole is false (deny-only SID) but the token type is Limited.
    /// Standard user: IsInRole false and the token is a plain default token.
    /// </summary>
    public static bool Classify(bool inAdminRole, int elevationType) => inAdminRole || elevationType == ElevationLimited;

    private static int ElevationType(WindowsIdentity id)
    {
        var size = sizeof(int);
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            return GetTokenInformation(id.Token, TokenElevationType, buf, size, out _) ? Marshal.ReadInt32(buf) : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}

// ---- running the removal ---------------------------------------------------------------------------------

public enum UninstallOutcome { Succeeded, SucceededRestartRequired, Cancelled, Failed }

public sealed record UninstallResult(UninstallOutcome Outcome, int ExitCode, string Message)
{
    public bool Succeeded => Outcome is UninstallOutcome.Succeeded or UninstallOutcome.SucceededRestartRequired;
}

public interface IElevatedRunner
{
    /// <summary>Run with a UAC prompt. Returns the exit code, or null when the user declined the prompt.</summary>
    Task<int?> RunAsync(string exe, string arguments, CancellationToken ct);
}

public sealed class UacRunner : IElevatedRunner
{
    public async Task<int?> RunAsync(string exe, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), arguments)
        {
            Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)      // ERROR_CANCELLED: the UAC prompt was declined
        {
            return null;
        }
    }
}

public static class UninstallResults
{
    /// <summary>Plain-English meaning of the exit codes wusa and DISM return.</summary>
    public static UninstallResult Map(int? exit) => exit switch
    {
        null => new(UninstallOutcome.Cancelled, 1223, "The Windows permission prompt (UAC) was declined, so nothing was changed."),
        0 => new(UninstallOutcome.Succeeded, 0, "The update was removed."),
        3010 => new(UninstallOutcome.SucceededRestartRequired, 3010, "The update was removed. Restart the computer to finish."),
        1641 => new(UninstallOutcome.SucceededRestartRequired, 1641, "The update was removed and a restart was started."),
        unchecked((int)0x800F0825) => new(UninstallOutcome.Failed, exit.Value, "Windows reports this update is permanent and cannot be removed."),
        unchecked((int)0x80240017) => new(UninstallOutcome.Failed, exit.Value, "Windows reports the update is not applicable or not installed."),
        2359303 or unchecked((int)0x00240007) => new(UninstallOutcome.Failed, exit.Value, "Windows reports the update is not installed."),
        5 => new(UninstallOutcome.Failed, 5, "Access was denied. Run PatchRadar as an administrator account."),
        87 => new(UninstallOutcome.Failed, 87, "DISM rejected the command (invalid parameter)."),
        _ => new(UninstallOutcome.Failed, exit.Value, $"Windows returned code 0x{exit.Value:X8}. The update may still be installed."),
    };
}

// ---- who removed what, and when -----------------------------------------------------------------------------

public sealed class UninstallAuditEntry
{
    public string Id { get; set; } = "";
    /// <summary>"Started" before the command runs, then Succeeded / SucceededRestartRequired / Cancelled / Failed.</summary>
    public string Stage { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public string TimestampLocal { get; set; } = "";
    public string User { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Kb { get; set; } = "";
    public string Title { get; set; } = "";
    public string Method { get; set; } = "";
    public string Command { get; set; } = "";
    public int? ExitCode { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Free text the user typed (a ticket number or reason).</summary>
    public string Reason { get; set; } = "";
    public string AppVersion { get; set; } = "";
}

/// <summary>Append-only JSON Lines file. A local record for accountability; it is not tamper-proof.</summary>
public sealed class UninstallAudit(string path, ILog? log = null)
{
    private readonly object _lock = new();
    private readonly ILog _log = log ?? NullLog.Instance;
    public string FilePath => path;

    public void Append(UninstallAuditEntry e)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(e) + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("Could not write the uninstall audit record", ex);
                throw;      // the removal must not proceed if it cannot be recorded
            }
        }
    }

    public IReadOnlyList<UninstallAuditEntry> ReadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return Array.Empty<UninstallAuditEntry>();
            var list = new List<UninstallAuditEntry>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<UninstallAuditEntry>(line) is { } e) list.Add(e); }
                catch (JsonException) { /* skip a damaged line, keep the rest */ }
            }
            return list;
        }
    }
}

// ---- the service the UI talks to ---------------------------------------------------------------------------------

public sealed class UninstallService
{
    private readonly UninstallPlanner _planner;
    private readonly IElevatedRunner _runner;
    private readonly IPrivilegeInfo _who;
    private readonly UninstallAudit _audit;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<InstalledUpdate>> _installed;
    private readonly ILog _log;
    private readonly Func<DateTime> _now;

    public UninstallService(UninstallPlanner planner, IElevatedRunner runner, IPrivilegeInfo who, UninstallAudit audit,
        Func<AppSettings> settings, Func<IReadOnlyList<InstalledUpdate>> installed, ILog? log = null, Func<DateTime>? now = null)
    {
        _planner = planner; _runner = runner; _who = who; _audit = audit; _settings = settings; _installed = installed;
        _log = log ?? NullLog.Instance; _now = now ?? (() => DateTime.UtcNow);
    }

    public UninstallAudit Audit => _audit;

    /// <summary>Feature switched on in Settings AND the person is an administrator. Standard users never see any of it.</summary>
    public bool IsAvailable => _settings().Advanced.EnableUninstall && _who.IsAdminCapable;

    /// <summary>Null when the button must not be shown (feature off, standard user, or nothing removable).</summary>
    public UninstallPlan? PlanIfOffered(InstalledUpdate update)
    {
        if (!IsAvailable) return null;
        var plan = _planner.Plan(update);
        return plan.CanUninstall ? plan : null;
    }

    public async Task<UninstallResult> ExecuteAsync(InstalledUpdate update, string reason, CancellationToken ct)
    {
        // Re-check at the moment of action: settings or rights may have changed since the window opened.
        if (!IsAvailable) throw new InvalidOperationException("Uninstalling updates is not enabled for this user.");
        var plan = _planner.Plan(update);
        if (!plan.CanUninstall) throw new InvalidOperationException("This update cannot be uninstalled: " + plan.Reason);

        var id = Guid.NewGuid().ToString("N");
        UninstallAuditEntry Entry(string stage, int? exit = null, string message = "")
        {
            var t = _now();
            return new UninstallAuditEntry
            {
                Id = id, Stage = stage, TimestampUtc = t, TimestampLocal = t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"),
                User = _who.UserName, Machine = _who.MachineName, Kb = update.Kb, Title = update.Title,
                Method = plan.Method.ToString(), Command = plan.Command, ExitCode = exit, Message = message,
                Reason = reason.Trim(), AppVersion = AppVersion.Running().ToString(),
            };
        }

        // The intent is written first. If that fails nothing is run.
        _audit.Append(Entry("Started"));
        _log.Warn($"Uninstall requested by {_who.UserName} on {_who.MachineName}: {update.Kb} via {plan.Method} ({plan.Command})");

        UninstallResult result;
        try
        {
            var exit = await _runner.RunAsync(plan.Executable, plan.Arguments, ct).ConfigureAwait(false);
            result = UninstallResults.Map(exit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("Uninstall could not be started", ex);
            result = new UninstallResult(UninstallOutcome.Failed, -1, "The removal command could not be started: " + ex.Message);
        }

        try { _audit.Append(Entry(result.Outcome.ToString(), result.ExitCode, result.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* already logged; the result still matters more */ }
        _log.Warn($"Uninstall of {update.Kb} finished: {result.Outcome} (code {result.ExitCode})");
        return result;
    }
}
