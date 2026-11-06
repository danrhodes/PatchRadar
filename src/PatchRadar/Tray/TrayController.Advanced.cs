using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PatchRadar.AppUpdate;
using PatchRadar.Core;
using PatchRadar.Infra;

namespace PatchRadar.Tray;

// Extra menu items for administrators. Hidden unless Shift or Alt is held as the menu opens.
public sealed partial class TrayController
{
    private void SyncAdvanced()
    {
        var show = HeldKeys.ShiftOrAlt();
        foreach (var c in _advanced) c.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAdvancedItems(ContextMenu menu)
    {
        _advanced.Clear();
        void Add(Control c) { c.Visibility = Visibility.Collapsed; menu.Items.Add(c); _advanced.Add(c); }
        void Item(string header, Action click, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => Later(click);
            Add(mi);
        }

        var exe = Environment.ProcessPath;
        var isOwnExe = exe is not null && Path.GetFileName(exe).StartsWith("PatchRadar", StringComparison.OrdinalIgnoreCase);

        Add(new Separator());
        Add(new MenuItem { Header = "Advanced (hold Shift or Alt)", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        Item("Copy diagnostics info", CopyDiagnostics);
        Item("Run self-check (--diag)…", () => RunDiag(exe), isOwnExe);
        Item("Scan the log for errors to report…",() => CheckProblemsNow?.Invoke(), CheckProblemsNow is not null);
        Item("Create debug screenshots…", () => _ = CaptureDebugWindowsAsync());
        Item("Send a test notification", () => ShowToast("PatchRadar test", "If you can read this, notifications work."));
        Item("Open settings and data folder", () => OpenFolder(Paths.AppData));
        Item("Open install folder", () => { if (exe is not null) OpenFolder(Path.GetDirectoryName(exe)!); }, exe is not null);
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _log.Warn("Could not open " + dir + ": " + ex.Message);
        }
    }

    private void CopyDiagnostics()
    {
        try { Clipboard.SetText(BuildDiagnostics()); ShowToast("Diagnostics copied", "Paste it into an email or an issue."); }
        catch (System.Runtime.InteropServices.ExternalException ex) { _log.Warn("Could not copy diagnostics: " + ex.Message); }
    }

    private string BuildDiagnostics()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return string.Join(Environment.NewLine,
            $"PatchRadar {AppVersion.Running()} ({AppVersion.Flavour()}){(AppVersion.Running().IsDevBuild ? " development build" : "")}",
            $"Windows {Environment.OSVersion.Version} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}){(_isServer ? ", Server" : "")}",
            $"Elevated: {elevated}",
            $"Exe: {Environment.ProcessPath}",
            $"Settings: {Paths.SettingsFile}",
            $"Logs: {FileLog.DefaultDirectory}",
            $"Last scan: {(_last.ScannedAt == DateTime.MinValue ? "never" : _last.ScannedAt.ToString("yyyy-MM-dd HH:mm"))}, {_last.Rows.Count} installed, {_last.Pending.Count} pending",
            $"Up {Uptime.Describe(Uptime.Current())}");
    }

    /// <summary>Runs the same headless check as <c>PatchRadar.exe --diag</c> in a second process, then opens the result.</summary>
    private void RunDiag(string? exe)
    {
        if (exe is null) return;
        var outFile = Path.Combine(Path.GetTempPath(), $"patchradar-diag-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        ShowToast("Self-check running", "This takes a little while. The result opens when it is done.");
        _ = Task.Run(() =>
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, $"--diag \"{outFile}\"") { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(TimeSpan.FromMinutes(3));
                if (File.Exists(outFile)) Process.Start(new ProcessStartInfo(outFile) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _log.Warn("Self-check failed to run: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Opens PatchRadar's real windows with this PC's real data, one after another, and saves each as a PNG in a new Desktop folder,
    /// with a short diagnostics file. Windows that were already open are captured and left open. The user reviews the folder before sharing.
    /// There is no folder picker: a tray app has no window to own one, and it was closing as the menu closed.
    /// </summary>
    private async Task CaptureDebugWindowsAsync()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"PatchRadar debug screenshots {DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            Directory.CreateDirectory(dir);
            ShowToast("Creating debug screenshots", "Windows will open and close for about 15 seconds. Please leave them alone. The folder opens when it is done.");
            var taken = 0;

            async Task Snap(string name, Window? w, bool wasOpen, int settleMs = 700)
            {
                if (w is null) return;
                try
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
                    await Task.Delay(settleMs);
                    WindowCapture.SavePng(w, Path.Combine(dir, name + ".png"));
                    taken++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _log.Warn($"Could not capture {name}: {ex.Message}");
                }
                finally { if (!wasOpen) w.Close(); }
            }

            // All updates (loads its list from the cache, so give it longer).
            var allOpen = _allWindow is { IsLoaded: true };
            ShowAllUpdates();
            await Snap("all-updates", _allWindow, allOpen, 2000);

            // The worst few updates: red first, then orange, grey, green.
            var worst = _last.Pending.Concat(_last.Rows).Where(r => !r.Ignored)
                .OrderBy(r => r.Severity switch { Severity.Red => 0, Severity.Orange => 1, Severity.Grey => 2, _ => 3 })
                .Take(3).ToList();
            foreach (var row in worst)
            {
                var open = _details.TryGetValue(row.Update.Kb, out var existing) && existing.IsLoaded;
                ShowDetail(row);
                await Snap("detail-" + (row.Update.HasKb ? row.Update.Kb : "no-kb"), _details.GetValueOrDefault(row.Update.Kb), open);
            }

            var digestOpen = _digestWindow is { IsLoaded: true };
            ShowDigest();
            await Snap("whats-changed", _digestWindow, digestOpen);

            if (_last.Pending.Count > 0) await Snap("pending-briefing", OpenBriefing(), wasOpen: false);

            var settingsOpen = _settingsWindow is { IsLoaded: true };
            ShowSettings();
            await Snap("settings", _settingsWindow, settingsOpen);

            var aboutOpen = _about is { IsLoaded: true };
            ShowAbout();
            await Snap("about", _about, aboutOpen);

            File.WriteAllText(Path.Combine(dir, "diagnostics.txt"), ProblemScrubber.ScrubCurrent(BuildDiagnostics()));
            ShowToast("Debug screenshots saved", $"{taken} images in {Path.GetFileName(dir)}. Check them before sharing.");
            OpenFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("Debug screenshots failed: " + ex.Message);
            ShowToast("Debug screenshots failed", ex.Message);
        }
    }
}
