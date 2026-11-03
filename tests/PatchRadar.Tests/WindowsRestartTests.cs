using PatchRadar.Infra;

namespace PatchRadar.Tests;

public class WindowsRestartTests
{
    [Fact]
    public void Arguments_use_restart_with_delay_and_comment()
    {
        var a = WindowsRestart.RestartArguments(900);
        Assert.StartsWith("/r /t 900 ", a);
        Assert.Contains(WindowsRestart.Comment, a);
    }

    [Fact]
    public void Negative_delay_is_clamped_to_zero() =>
        Assert.StartsWith("/r /t 0 ", WindowsRestart.RestartArguments(-5));

    [Fact]
    public void Next_2am_is_today_when_before_2am()
    {
        var now = new DateTime(2026, 11, 3, 0, 30, 0);
        Assert.Equal(90 * 60, WindowsRestart.SecondsUntilNext(now, 2));
    }

    [Fact]
    public void Next_2am_is_tomorrow_when_after_2am()
    {
        var now = new DateTime(2026, 11, 3, 14, 0, 0);
        Assert.Equal(12 * 3600, WindowsRestart.SecondsUntilNext(now, 2));
    }

    [Fact]
    public void Exactly_2am_rolls_to_tomorrow() =>
        Assert.Equal(24 * 3600, WindowsRestart.SecondsUntilNext(new DateTime(2026, 11, 3, 2, 0, 0), 2));
}
