using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>The session's VNC password is not rotated under a running session: a device that reported its tunnel
/// up within the telemetry window keeps its password past the hour.</summary>
public class TunnelActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_device_that_reported_its_tunnel_up_is_up_until_the_report_goes_stale()
    {
        var a = new TunnelActivity();
        Assert.False(a.IsUp("dev", T0));
        a.Report("dev", true, T0);
        Assert.True(a.IsUp("dev", T0.AddMinutes(1)));
        Assert.True(a.IsUp("dev", T0 + TunnelActivity.Fresh - TimeSpan.FromSeconds(1)));
        Assert.False(a.IsUp("dev", T0 + TunnelActivity.Fresh));
    }

    [Fact]
    public void A_tunnel_reported_down_is_forgotten_at_once()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0);
        a.Report("dev", false, T0.AddSeconds(30));
        Assert.False(a.IsUp("dev", T0.AddSeconds(31)));
    }

    [Fact]
    public void A_report_from_the_future_does_not_count()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0.AddMinutes(5));
        Assert.False(a.IsUp("dev", T0));
    }
}
