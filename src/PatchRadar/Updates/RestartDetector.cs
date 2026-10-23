using System.IO;
using Microsoft.Win32;

namespace PatchRadar.Updates;

public interface IRestartDetector
{
    bool IsRestartPending();
}

/// <summary>Windows marks a finished-but-not-applied update with one of these registry keys.</summary>
public sealed class RegistryRestartDetector : IRestartDetector
{
    private static readonly string[] Keys =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
    };

    public bool IsRestartPending() => Check(KeyExists);

    public static bool Check(Func<string, bool> keyExists) => Keys.Any(keyExists);

    private static bool KeyExists(string path)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }
}
