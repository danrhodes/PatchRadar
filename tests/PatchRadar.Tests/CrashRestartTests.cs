using PatchRadar.Infra;

namespace PatchRadar.Tests;

public class CrashRestartTests
{
    [Fact]
    public void Restarts_once_then_refuses_inside_the_window_and_allows_again_after()
    {
        var stamp = Path.Combine(Path.GetTempPath(), "pr-crash-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var t0 = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            Assert.True(CrashRestart.ShouldRestart(stamp, t0));
            Assert.False(CrashRestart.ShouldRestart(stamp, t0.AddMinutes(1)));
            Assert.True(CrashRestart.ShouldRestart(stamp, t0.AddMinutes(6)));
        }
        finally { File.Delete(stamp); }
    }
}
