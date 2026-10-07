using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>
/// A stamp can only look older than it is, never newer: a clock that is ahead is proven by one report, a
/// clock that is behind needs a second report that agrees, and a sleeping laptop's late stamp is outvoted
/// by its next fresh one.
/// </summary>
public class ClockSkewTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_clock_ahead_is_flagged_from_one_report()
    {
        var t = new ClockSkewTracker();
        Assert.Equal("clock-skew:+88", t.Problem("d", T0.AddSeconds(88), T0));
    }

    [Fact]
    public void A_clock_behind_needs_two_reports_that_agree()
    {
        var t = new ClockSkewTracker();
        Assert.Null(t.Problem("d", T0.AddSeconds(-34), T0));
        Assert.Equal("clock-skew:-34", t.Problem("d", T0.AddMinutes(1).AddSeconds(-34), T0.AddMinutes(1)));
    }

    [Fact]
    public void A_sleeping_laptops_late_stamp_is_outvoted_by_its_next_fresh_one()
    {
        var t = new ClockSkewTracker();
        Assert.Null(t.Problem("d", T0.AddSeconds(-34), T0));                                 // delivered late
        Assert.Null(t.Problem("d", T0.AddMinutes(1).AddSeconds(-1), T0.AddMinutes(1)));       // fresh: the clock is fine
    }

    [Fact]
    public void Small_offsets_are_fine_and_the_sign_is_always_written()
    {
        var t = new ClockSkewTracker();
        Assert.Null(t.Problem("d", T0.AddSeconds(29), T0));
        Assert.Null(t.Problem("d", T0.AddSeconds(-29), T0));
        Assert.Null(t.Problem("d", T0.AddMinutes(1).AddSeconds(-29), T0.AddMinutes(1)));
        Assert.Equal("clock-skew:+30", t.Problem("d", T0.AddMinutes(2).AddSeconds(30), T0.AddMinutes(2)));
    }

    [Fact]
    public void Samples_older_than_the_window_no_longer_count()
    {
        var t = new ClockSkewTracker();
        Assert.Null(t.Problem("d", T0.AddSeconds(-40), T0));
        // Six minutes later the first sample is gone: this is again a lone "behind" report.
        Assert.Null(t.Problem("d", T0.AddMinutes(6).AddSeconds(-40), T0.AddMinutes(6)));
        Assert.Equal("clock-skew:-40", t.Problem("d", T0.AddMinutes(7).AddSeconds(-40), T0.AddMinutes(7)));
    }

    [Fact]
    public void Devices_are_tracked_apart_and_an_old_agent_without_a_stamp_is_ignored()
    {
        var t = new ClockSkewTracker();
        Assert.Null(t.Problem("a", T0.AddSeconds(-40), T0));
        Assert.Null(t.Problem("b", T0.AddSeconds(-40), T0));       // b's first report, not a's second
        Assert.Null(t.Problem("c", default, T0));
    }
}
