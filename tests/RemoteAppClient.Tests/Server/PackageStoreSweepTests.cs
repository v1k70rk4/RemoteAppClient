using RemoteServer.Data.Entities;
using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>The sweep keeps the current and previous package of every channel and component, drops the rest,
/// and deletes only package files nothing kept points at.</summary>
public class PackageStoreSweepTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static ReleasePackage Pkg(string channel, string component, string version, string file, int day) =>
        new() { Channel = channel, Component = component, Version = version, FileName = file, UploadedAt = T0.AddDays(day) };

    [Fact]
    public void The_two_newest_of_a_lane_stay_and_the_older_ones_go_with_their_files()
    {
        var records = new[]
        {
            Pkg("beta", "agent", "2.3.0.4", "agent-beta-2.3.0.4.exe", 1),
            Pkg("beta", "agent", "2.3.1.0", "agent-beta-2.3.1.0.exe", 2),
            Pkg("beta", "agent", "3.0.0.1", "agent-beta-3.0.0.1-037eebe7ea5a.exe", 3),
            Pkg("beta", "agent", "2.2.7.1", "agent-beta-2.2.7.1.exe", 0),
        };
        var files = records.Select(r => r.FileName).ToList();
        var (stale, staleFiles) = PackageStoreSweep.Plan(records, files);
        Assert.Equal(["2.3.0.4", "2.2.7.1"], stale.Select(r => r.Version).OrderDescending());
        Assert.Equal(["agent-beta-2.2.7.1.exe", "agent-beta-2.3.0.4.exe"], staleFiles.Order());
    }

    [Fact]
    public void A_file_promoted_to_another_channel_stays_while_that_channel_keeps_it()
    {
        var records = new[]
        {
            Pkg("beta", "agent", "3.0.0.0", "agent-beta-3.0.0.0-aaaa.exe", 1),
            Pkg("beta", "agent", "3.0.0.1", "agent-beta-3.0.0.1-bbbb.exe", 2),
            Pkg("beta", "agent", "3.0.0.2", "agent-beta-3.0.0.2-cccc.exe", 3),
            Pkg("rtm", "agent", "3.0.0.0", "agent-beta-3.0.0.0-aaaa.exe", 4),   // promoted: shares beta's file
        };
        var files = new[] { "agent-beta-3.0.0.0-aaaa.exe", "agent-beta-3.0.0.1-bbbb.exe", "agent-beta-3.0.0.2-cccc.exe" };
        var (stale, staleFiles) = PackageStoreSweep.Plan(records, files);
        Assert.Single(stale);
        Assert.Equal("beta", stale[0].Channel);
        Assert.Equal("3.0.0.0", stale[0].Version);
        Assert.Empty(staleFiles);   // rtm still points at it
    }

    [Fact]
    public void Orphan_package_files_go_and_everything_else_in_the_directory_is_left_alone()
    {
        var records = new[] { Pkg("beta", "client", "3.0.0.3", "client-beta-3.0.0.3-37ec1363773a.exe", 1) };
        var files = new[]
        {
            "client-beta-3.0.0.3-37ec1363773a.exe", "client-beta-2.2.6.0.exe", "vnc-beta-2.8.88.msi", "updater-beta-2.1.6.0.exe",
            "Coimbra_ITS_RemoteAppClient_all.msi", "msi-build", "notes.txt",
        };
        var (stale, staleFiles) = PackageStoreSweep.Plan(records, files);
        Assert.Empty(stale);
        Assert.Equal(["client-beta-2.2.6.0.exe", "updater-beta-2.1.6.0.exe", "vnc-beta-2.8.88.msi"], staleFiles.Order());
    }

    [Fact]
    public void Lanes_are_matched_regardless_of_case()
    {
        var records = new[]
        {
            Pkg("Beta", "Agent", "1", "agent-beta-1.exe", 1),
            Pkg("beta", "agent", "2", "agent-beta-2.exe", 2),
            Pkg("beta", "agent", "3", "agent-beta-3.exe", 3),
        };
        var (stale, _) = PackageStoreSweep.Plan(records, []);
        Assert.Single(stale);
        Assert.Equal("1", stale[0].Version);
    }
}
