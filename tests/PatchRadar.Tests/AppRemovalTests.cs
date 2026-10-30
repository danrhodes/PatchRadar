using PatchRadar.Infra;

namespace PatchRadar.Tests;

public class AppRemovalTests
{
    [Fact]
    public void Script_deletes_exe_leftovers_data_and_itself()
    {
        var s = AppRemoval.BuildScript(@"C:\Users\a\Downloads\PatchRadar-v1.2.1-win-x64.exe", new[] { @"C:\Users\a\AppData\Local\PatchRadar" });
        Assert.Contains(@"del /f /q ""C:\Users\a\Downloads\PatchRadar-v1.2.1-win-x64.exe""", s);
        Assert.Contains(@"-win-x64.exe.old""", s);
        Assert.Contains(@"rmdir /s /q ""C:\Users\a\AppData\Local\PatchRadar""", s);
        Assert.Contains(@"del ""%~f0""", s);
    }

    [Fact]
    public void Script_without_an_exe_or_data_only_removes_itself()
    {
        var s = AppRemoval.BuildScript(null, Array.Empty<string>());
        Assert.DoesNotContain("rmdir", s);
        Assert.DoesNotContain("PatchRadar", s);
    }

    [Fact]
    public void Percent_signs_in_paths_are_escaped()
    {
        Assert.Contains(@"C:\100%%\PatchRadar.exe", AppRemoval.BuildScript(@"C:\100%\PatchRadar.exe", Array.Empty<string>()));
    }
}
