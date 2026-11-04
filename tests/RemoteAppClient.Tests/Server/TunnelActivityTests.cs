using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>The session's VNC password is not rotated under a running VNC session: a device that reports its
/// VNC forward up keeps its password past the hour. The evidence is the agent's own report, every minute: a
/// file-only tunnel reports none, a stale report counts as closed.</summary>
public class TunnelActivityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_vnc_forward_reported_up_keeps_the_session_until_the_report_goes_stale()
    {
        var a = new TunnelActivity();
        Assert.False(a.IsVncSessionUp("dev", T0));
        a.Report("dev", true, T0);
        Assert.True(a.IsVncSessionUp("dev", T0.AddMinutes(1)));
        Assert.True(a.IsVncSessionUp("dev", T0 + TunnelActivity.Fresh - TimeSpan.FromSeconds(1)));
        Assert.False(a.IsVncSessionUp("dev", T0 + TunnelActivity.Fresh));
    }

    [Fact]
    public void A_session_running_for_hours_keeps_its_password_as_long_as_the_reports_keep_coming()
    {
        var a = new TunnelActivity();
        for (var t = T0; t <= T0.AddHours(2); t = t.AddMinutes(1)) a.Report("dev", true, t);
        Assert.True(a.IsVncSessionUp("dev", T0.AddHours(2).AddSeconds(30)));
    }

    [Fact]
    public void A_report_without_the_vnc_forward_ends_the_session_at_once()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0);
        a.Report("dev", false, T0.AddSeconds(30));   // the VNC tunnel gave way to a file-only one, or closed
        Assert.False(a.IsVncSessionUp("dev", T0.AddSeconds(31)));
    }

    [Fact]
    public void A_file_only_tunnel_never_starts_a_vnc_session()
    {
        var a = new TunnelActivity();
        a.Report("dev", false, T0);                   // tunnel up, no VNC forward: reported as false
        Assert.False(a.IsVncSessionUp("dev", T0.AddSeconds(5)));
    }

    [Fact]
    public void A_device_gone_quiet_counts_as_closed_until_it_reports_the_forward_up_again()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0);
        Assert.False(a.IsVncSessionUp("dev", T0.AddHours(2)));        // nothing heard for two hours
        a.Report("dev", false, T0.AddHours(2));                        // back with a file-only tunnel
        Assert.False(a.IsVncSessionUp("dev", T0.AddHours(2).AddSeconds(5)));
        a.Report("dev", true, T0.AddHours(2).AddMinutes(1));           // the VNC forward is up: that is a session
        Assert.True(a.IsVncSessionUp("dev", T0.AddHours(2).AddMinutes(1).AddSeconds(5)));
    }

    [Fact]
    public void A_report_from_the_future_does_not_count()
    {
        var a = new TunnelActivity();
        a.Report("dev", true, T0.AddMinutes(5));
        Assert.False(a.IsVncSessionUp("dev", T0));
    }
}
