using PatchRadar.Infra;
using Xunit;

namespace PatchRadar.Tests;

public class SelfInstallTests
{
    [Theory]
    [InlineData(@"C:\Users\a\Downloads\PatchRadar-v1.2.1-win-x64.exe")]
    [InlineData(@"C:\Users\a\Desktop\PatchRadar.exe")]
    [InlineData(@"C:\Tools\PatchRadar.exe")]
    public void Exe_outside_install_folder_relocates(string path)
        => Assert.True(SelfInstall.ShouldRelocate(path));

    [Fact]
    public void Installed_copy_never_relocates()
        => Assert.False(SelfInstall.ShouldRelocate(SelfInstall.InstalledPath));

    [Fact]
    public void Host_exe_is_never_moved()
        => Assert.False(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\dotnet.exe"));

    [Fact]
    public void Custom_dir_requests_move_there()
        => Assert.True(SelfInstall.ShouldRelocate(@"C:\Users\a\Downloads\PatchRadar.exe", @"D:\Apps\PR"));

    [Fact]
    public void Already_in_custom_dir_stays()
        => Assert.False(SelfInstall.ShouldRelocate(@"D:\Apps\PR\PatchRadar.exe", @"D:\Apps\PR"));

    [Fact]
    public void Remembered_custom_dir_is_respected()
        => Assert.False(SelfInstall.ShouldRelocate(@"D:\Apps\PR\PatchRadar.exe", null, @"D:\Apps\PR"));

    [Fact]
    public void Parses_customdir_argument()
    {
        Assert.Equal(@"D:\Apps\PR", SelfInstall.ParseCustomDir(new[] { "--customdir", @"D:\Apps\PR" }));
        Assert.Null(SelfInstall.ParseCustomDir(new[] { "--customdir" }));
        Assert.Null(SelfInstall.ParseCustomDir(Array.Empty<string>()));
    }
}
