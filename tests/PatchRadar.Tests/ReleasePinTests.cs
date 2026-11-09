using PatchRadar.Infra;
using Xunit;

namespace PatchRadar.Tests;

public class ReleasePinTests
{
    [Theory]
    [InlineData(26100, "24H2")]
    [InlineData(26200, "25H2")]
    [InlineData(26300, "26H2")]
    [InlineData(26300 + 9818, "26H2")]
    [InlineData(22631, "23H2")]
    public void Maps_build_to_release(int build, string release) => Assert.Equal(release, ReleasePin.RunningRelease(build));

    [Fact]
    public void Windows_10_has_no_release_mapping() => Assert.Null(ReleasePin.RunningRelease(19045));

    [Fact]
    public void Pin_older_than_running_is_outdated() => Assert.True(ReleasePin.IsOutdated(26300, "24H2"));

    [Theory]
    [InlineData(26300, "26H2")]
    [InlineData(26300, "27H1")]
    [InlineData(26100, "24H2")]
    [InlineData(26300, "garbage")]
    [InlineData(26300, null)]
    [InlineData(19045, "22H2")]
    public void Current_future_or_unreadable_pins_are_not_flagged(int build, string? pin) => Assert.False(ReleasePin.IsOutdated(build, pin));

    [Theory]
    [InlineData("24H2", "25H1")]
    [InlineData("25H1", "25H2")]
    [InlineData("25H2", "26H1")]
    public void Next_release_steps_through_half_years(string from, string to) => Assert.Equal(to, ReleasePin.Next(from));

    [Fact]
    public void Next_of_garbage_is_null() => Assert.Null(ReleasePin.Next("nope"));

    [Fact]
    public async Task Finds_newest_published_release_even_with_gaps()
    {
        var published = new HashSet<string> { "25H2", "26H2" };   // no H1 releases
        Assert.Equal("26H2", await ReleasePin.NewestAfterAsync("24H2", r => Task.FromResult(published.Contains(r))));
    }

    [Fact]
    public async Task Nothing_newer_published_gives_null()
        => Assert.Null(await ReleasePin.NewestAfterAsync("26H2", _ => Task.FromResult(false)));

    [Fact]
    public async Task A_failed_lookup_propagates_rather_than_reading_as_no()
        => await Assert.ThrowsAsync<HttpRequestException>(() => ReleasePin.NewestAfterAsync("24H2", _ => throw new HttpRequestException("offline")));

    [Fact]
    public void Message_names_pin_newest_and_running()
    {
        var m = ReleasePin.Message(26300, "24H2", "26H2");
        Assert.Contains("24H2", m);
        Assert.Contains("26H2", m);
        Assert.Contains("26300", m);
    }
}
