using System.IO;
using Microsoft.Win32;

namespace PatchRadar.Infra;

/// <summary>Registry Run-key access, abstracted so the toggle can be tested without touching HKCU.</summary>
public interface IRunKey
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}

public sealed class RegistryRunKey : IRunKey
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(name) as string;
    }

    public void Set(string name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Run at login via HKCU Run. The value is removed when disabled.</summary>
public sealed class Autostart(IRunKey key, string exePath, ILog? log = null)
{
    public const string ValueName = "PatchRadar";

    private string Command => $"\"{exePath}\"";

    public bool IsEnabled => string.Equals(key.Get(ValueName), Command, StringComparison.OrdinalIgnoreCase);

    /// <summary>Make the registry match the setting. Also repairs a stale path after the exe moved.</summary>
    public void Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (!IsEnabled) { key.Set(ValueName, Command); log?.Info("Run at login enabled"); }
            }
            else if (key.Get(ValueName) is not null)
            {
                key.Delete(ValueName);
                log?.Info("Run at login disabled");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            log?.Warn("Could not update the Run key: " + ex.Message);
        }
    }
}
