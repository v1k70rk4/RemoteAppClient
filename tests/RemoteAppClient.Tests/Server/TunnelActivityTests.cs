using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>The session's VNC password is not rotated under a running VNC session: a device that was sent a VNC
/// open and has reported its tunnel up since keeps its password past the hour. A file-only tunnel, or a tunnel
/// reported down after the open, does not keep it.</summary>
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

    [Fact]
    public void A_vnc_session_is_up_once_the_tunnel_is_reported_up_after_the_vnc_open()
    {
        var a = new TunnelActivity();
        a.VncOpened("dev", T0);
        Assert.False(a.IsVncSessionUp("dev", T0.AddSeconds(5)));          // nothing reported yet
        a.Report("dev", true, T0.AddMinutes(1));
        Assert.True(a.IsVncSessionUp("dev", T0.AddMinutes(1).AddSeconds(5)));
        Assert.False(a.IsVncSessionUp("dev", T0.AddHours(2)));             // the report went stale meanwhile
        a.Report("dev", true, T0.AddHours(2));
        Assert.True(a.IsVncSessionUp("dev", T0.AddHours(2).AddSeconds(5))); // still up two hours in: password kept
    }

    [Fact]
    public void A_file_only_tunnel_is_up_but_is_no_vnc_session()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0);
        Assert.True(a.IsUp("dev", T0.AddSeconds(5)));
        Assert.False(a.IsVncSessionUp("dev", T0.AddSeconds(5)));
    }

    [Fact]
    public void An_up_report_from_before_the_vnc_open_is_the_previous_tunnel_not_this_session()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0);                 // a file tunnel, say
        a.VncOpened("dev", T0.AddSeconds(30));
        Assert.False(a.IsVncSessionUp("dev", T0.AddSeconds(40)));
    }

    [Fact]
    public void A_tunnel_reported_down_ends_the_vnc_session_until_the_next_vnc_open()
    {
        var a = new TunnelActivity();
        a.VncOpened("dev", T0);
        a.Report("dev", true, T0.AddMinutes(1));
        a.Report("dev", false, T0.AddMinutes(2));
        a.Report("dev", true, T0.AddMinutes(3));   // a later tunnel, nobody asked for VNC on it
        Assert.False(a.IsVncSessionUp("dev", T0.AddMinutes(3).AddSeconds(5)));
    }
}
