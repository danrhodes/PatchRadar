using PatchRadar.Infra;
using Xunit;

namespace PatchRadar.Tests;

public class SelfInstallTests
{
    private static readonly string[] Dl = { @"C:\Users\a\Downloads" };

    [Fact]
    public void Exe_in_downloads_relocates()
        => Assert.True(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\PatchRadar-v1.2.1-win-x64.exe", Dl));

    [Fact]
    public void Exe_in_downloads_subfolder_relocates()
        => Assert.True(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\unzipped\PatchRadar.exe", Dl));

    [Fact]
    public void Exe_elsewhere_stays()
        => Assert.False(SelfInstall.ShouldRelocate(@"C:\Tools\PatchRadar.exe", Dl));

    [Fact]
    public void Sibling_folder_with_same_prefix_is_not_downloads()
        => Assert.False(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads2\PatchRadar.exe", Dl));

    [Fact]
    public void Installed_copy_never_relocates()
        => Assert.False(SelfInstall.ShouldRelocate(SelfInstall.InstalledPath, new[] { Paths.LocalAppData }));

    [Fact]
    public void Host_exe_is_never_moved()
        => Assert.False(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\dotnet.exe", Dl));

    [Fact]
    public void Missing_folder_is_ignored()
        => Assert.False(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\PatchRadar.exe", new string?[] { null, "" }));
}
