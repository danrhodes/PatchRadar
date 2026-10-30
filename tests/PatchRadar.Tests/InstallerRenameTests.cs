using PatchRadar.AppUpdate;

namespace PatchRadar.Tests;

public class InstallerRenameTests
{
    private static AppVersion V(string s) => AppVersion.TryParse(s, out var v) ? v : throw new ArgumentException(s);

    [Theory]
    [InlineData(@"C:\Downloads\PatchRadar-v1.1.0-win-x64.exe", @"C:\Downloads\PatchRadar-v1.2.1-win-x64.exe")]
    [InlineData(@"C:\Downloads\PatchRadar-v1.1.0-win-x64-slim.exe", @"C:\Downloads\PatchRadar-v1.2.1-win-x64-slim.exe")]
    [InlineData(@"C:\Downloads\PatchRadar-v1.1.0-pre-release.2-win-x64.exe", @"C:\Downloads\PatchRadar-v1.2.1-win-x64.exe")]
    [InlineData(@"C:\Apps\PatchRadar.exe", @"C:\Apps\PatchRadar.exe")]
    public void Filename_follows_the_new_version(string current, string expected) =>
        Assert.Equal(expected, UpdateInstaller.RenamedFor(current, V("1.2.1")));
}
