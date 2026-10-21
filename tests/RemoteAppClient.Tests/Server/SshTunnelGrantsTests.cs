using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>The grants file the bastion's sshd reads: a device appears for two minutes after its open-tunnel was
/// delivered, with its two ports and the time; then it is gone.</summary>
public class SshTunnelGrantsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_grant_is_one_tab_separated_line_with_the_time()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant> { ["dev-a"] = new(50023, 60023, T0) };
        Assert.Equal($"dev-a\t50023\t60023\t{T0.ToUnixTimeSeconds()}\n", SshTunnelGrants.Render(grants, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_file_session_is_granted_the_file_port_alone()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant> { ["dev-a"] = new(0, 60023, T0) };
        Assert.Equal($"dev-a\t0\t60023\t{T0.ToUnixTimeSeconds()}\n", SshTunnelGrants.Render(grants, T0));
    }

    [Fact]
    public void A_file_grant_keeps_the_vnc_port_of_a_standing_grant_and_a_stale_one_is_replaced()
    {
        var vnc = new SshTunnelGrants.Grant(50023, 60023, T0);
        var merged = SshTunnelGrants.Merge(vnc, 0, 60023, T0.AddSeconds(20));   // files asked while VNC still waits for consent
        Assert.Equal((50023, 60023), (merged.VncPort, merged.FilePort));
        Assert.Equal(T0.AddSeconds(20), merged.At);

        var afterWindow = SshTunnelGrants.Merge(vnc, 0, 60023, T0 + SshTunnelGrants.Window);
        Assert.Equal(0, afterWindow.VncPort);                                      // the old grant had run out

        Assert.Equal(50023, SshTunnelGrants.Merge(new(0, 60023, T0), 50023, 60023, T0.AddSeconds(5)).VncPort);   // VNC after files
        Assert.Equal(0, SshTunnelGrants.Merge(null, 0, 60023, T0).VncPort);
    }

    [Fact]
    public void A_grant_runs_out_after_the_window_and_the_rest_stay_sorted()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant>
        {
            ["dev-b"] = new(50001, 60001, T0),
            ["dev-a"] = new(50023, 60023, T0.AddMinutes(1)),
        };
        var now = T0 + SshTunnelGrants.Window;   // dev-b's two minutes are exactly up, dev-a has a minute left
        var text = SshTunnelGrants.Render(grants, now);
        Assert.Equal($"dev-a\t50023\t60023\t{T0.AddMinutes(1).ToUnixTimeSeconds()}\n", text);
        Assert.Equal("", SshTunnelGrants.Render(grants, T0.AddMinutes(10)));
    }
}
