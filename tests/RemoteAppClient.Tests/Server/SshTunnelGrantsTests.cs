using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>The grants file the bastion's sshd reads: a device appears for two minutes after its open-tunnel was
/// delivered, with its ports and each port's own time; a port that has run out shows as 0, a device with no live
/// port is gone.</summary>
public class SshTunnelGrantsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset None = DateTimeOffset.MinValue;

    [Fact]
    public void A_fresh_grant_is_one_tab_separated_line_with_both_times()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant> { ["dev-a"] = new(50023, 60023, T0, T0) };
        var t = T0.ToUnixTimeSeconds();
        Assert.Equal($"dev-a\t50023\t60023\t{t}\t{t}\n", SshTunnelGrants.Render(grants, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_file_session_is_granted_the_file_port_alone()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant> { ["dev-a"] = new(0, 60023, None, T0) };
        Assert.Equal($"dev-a\t0\t60023\t0\t{T0.ToUnixTimeSeconds()}\n", SshTunnelGrants.Render(grants, T0));
    }

    [Fact]
    public void A_file_grant_keeps_a_standing_vnc_port_on_its_own_clock()
    {
        var vnc = new SshTunnelGrants.Grant(50023, 60023, T0, T0);
        var merged = SshTunnelGrants.Merge(vnc, 0, 60023, T0.AddSeconds(20));   // files asked while VNC still waits for consent
        Assert.Equal((50023, 60023), (merged.VncPort, merged.FilePort));
        Assert.Equal(T0, merged.VncAt);                                            // not renewed by the file request
        Assert.Equal(T0.AddSeconds(20), merged.FileAt);

        // Repeated file requests cannot keep the VNC permission alive: at two minutes it is gone whatever happened since.
        var later = SshTunnelGrants.Merge(merged, 0, 60023, T0.AddSeconds(100));
        Assert.Equal(T0, later.VncAt);
        Assert.Equal(0, SshTunnelGrants.Merge(later, 0, 60023, T0 + SshTunnelGrants.Window).VncPort);

        Assert.Equal((50023, T0.AddSeconds(5)), (SshTunnelGrants.Merge(new(0, 60023, None, T0), 50023, 60023, T0.AddSeconds(5)) is var g ? (g.VncPort, g.VncAt) : default));
        Assert.Equal(0, SshTunnelGrants.Merge(null, 0, 60023, T0).VncPort);
    }

    [Fact]
    public void A_port_that_ran_out_shows_as_0_and_a_grant_with_no_live_port_is_gone()
    {
        var grants = new Dictionary<string, SshTunnelGrants.Grant>
        {
            ["dev-b"] = new(50001, 60001, T0, T0.AddMinutes(1)),                   // VNC asked at T0, files a minute later
            ["dev-a"] = new(50023, 60023, T0.AddMinutes(1), T0.AddMinutes(1)),
        };
        var now = T0 + SshTunnelGrants.Window;   // dev-b's VNC minutes are up, its file port has a minute left
        var text = SshTunnelGrants.Render(grants, now);
        var t1 = T0.AddMinutes(1).ToUnixTimeSeconds();
        Assert.Equal($"dev-a\t50023\t60023\t{t1}\t{t1}\ndev-b\t0\t60001\t0\t{t1}\n", text);
        Assert.Equal("", SshTunnelGrants.Render(grants, T0.AddMinutes(10)));
    }
}

/// <summary>The bastion script names the grants-file format it reads; the server compares it with the one it
/// writes, so a server update that changes the format is noticed before enforce turns devices away.</summary>
public class SshTunnelGrantsFormatTests
{
    [Fact]
    public void The_installed_script_names_its_format()
    {
        var script = "#!/bin/sh\n# AuthorizedPrincipalsCommand ...\n# rac-principals format 2 (deviceId, vncPort, ...)\nKEYID=\"$1\"\n";
        Assert.Equal(2, SshTunnelGrants.ScriptFormatOf(script));
    }

    [Fact]
    public void A_script_from_before_the_marker_is_told_apart_by_the_columns_it_reads()
    {
        Assert.Equal(1, SshTunnelGrants.ScriptFormatOf("#!/bin/sh\nKEYID=\"$1\"\nawk -F'\t' '$1 == id { print $2, $3, $4; exit }'\n"));
        Assert.Equal(2, SshTunnelGrants.ScriptFormatOf("#!/bin/sh\nKEYID=\"$1\"\nawk -F'\t' '$1 == id { print $2, $3, $4, $5; exit }'\n"));
    }

    [Fact]
    public void No_script_means_no_verdict()
    {
        Assert.Null(SshTunnelGrants.InstalledScriptFormat(Path.Combine(Path.GetTempPath(), "rac-no-such-script-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void The_step_in_the_repo_names_the_format_the_server_writes()
    {
        // deploy/steps/05-bastion.sh carries the script; its marker must follow SshTunnelGrants.FileFormat.
        var dir = AppContext.BaseDirectory;
        string? step = null;
        for (var d = new DirectoryInfo(dir); d is not null && step is null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "deploy", "steps", "05-bastion.sh");
            if (File.Exists(candidate)) step = candidate;
        }
        if (step is null) return;   // not run from the repo (a packaged test run): nothing to compare
        Assert.Equal(SshTunnelGrants.FileFormat, SshTunnelGrants.ScriptFormatOf(File.ReadAllText(step)));
    }
}
