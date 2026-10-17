using System.IO;

namespace PatchRadar.Infra;

public enum LogLevel { Debug, Information, Warning, Error }

public interface ILog
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

/// <summary>Rolling daily file log. Keeps the newest <c>keepFiles</c> files.</summary>
public sealed class FileLog : ILog
{
    private readonly string _dir;
    private readonly int _keep;
    private readonly object _gate = new();
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    public FileLog(string dir, int keepFiles = 14)
    {
        _dir = dir;
        _keep = keepFiles;
        try { Directory.CreateDirectory(_dir); Prune(); } catch { /* logging must never throw */ }
    }

    public static string DefaultDirectory =>
        Path.Combine(Paths.LocalAppData, "logs");

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Information, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? message : $"{message}: {ex}");

    private void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;
        try
        {
            var file = Path.Combine(_dir, $"patchradar-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(file, line);
        }
        catch { /* ignore */ }
    }

    private void Prune()
    {
        var old = new DirectoryInfo(_dir).GetFiles("patchradar-*.log")
            .OrderByDescending(f => f.Name).Skip(_keep);
        foreach (var f in old) { try { f.Delete(); } catch { } }
    }
}

public static class Paths
{
    public static string AppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PatchRadar");

    public static string LocalAppData => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PatchRadar");

    public static string SettingsFile => Path.Combine(AppData, "settings.json");
    public static string StateFile => Path.Combine(AppData, "state.json");
    public static string KbCacheDir => Path.Combine(LocalAppData, "cache", "kb");
}
