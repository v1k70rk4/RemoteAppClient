using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>A device auto-converge gave up on stays paused only while it reports what it reported then.</summary>
public class UpdatePauseStateTests
{
    [Fact]
    public void Paused_only_for_the_same_package_and_the_same_report()
    {
        var s = new UpdatePauseState();
        var dev = Guid.NewGuid();
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.85.0"));

        s.Set(dev, "vnc", "2.8.89.0", "2.8.85.0");
        Assert.True(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.85.0"));
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.87.0"));   // the device now reports something else
        Assert.False(s.IsPaused(dev, "vnc", "2.8.90.0", "2.8.85.0"));   // a newer package
        Assert.False(s.IsPaused(dev, "agent", "2.8.89.0", "2.8.85.0")); // another component
        Assert.False(s.IsPaused(Guid.NewGuid(), "vnc", "2.8.89.0", "2.8.85.0"));
    }

    [Fact]
    public void A_changed_report_discards_the_pause_so_returning_to_it_is_not_paused()
    {
        var s = new UpdatePauseState();
        var dev = Guid.NewGuid();
        s.Set(dev, "vnc", "2.8.89.0", "2.8.85.0");
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.87.0")); // the report moved on: pause dropped
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.85.0")); // back to the old report: a fresh attempt is due
    }

    [Fact]
    public void A_new_pause_replaces_the_old_and_clear_forgets_it()
    {
        var s = new UpdatePauseState();
        var dev = Guid.NewGuid();
        s.Set(dev, "vnc", "2.8.89.0", "2.8.85.0");
        s.Set(dev, "vnc", "2.8.89.0", "2.8.87.0");
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.85.0"));
        Assert.True(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.87.0"));
        s.Clear(dev);
        Assert.False(s.IsPaused(dev, "vnc", "2.8.89.0", "2.8.87.0"));
    }
}
