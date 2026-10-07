using RemoteAgent.Admin;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>The one word both consoles put on a device row, in the order the decision is made.</summary>
public class DeviceLivenessTests
{
    private static DeviceInfo Device(bool online = false, bool reporting = false, int reconnects = 0, string status = "Approved", string? problem = null) =>
        new() { DeviceId = "d", Hostname = "HOST", Online = online, Reporting = reporting, RecentReconnects = reconnects, Status = status, Problem = problem };

    [Fact]
    public void A_known_problem_outranks_everything()
    {
        Assert.Equal(DeviceState.Error, DeviceLiveness.Of(Device(online: true, reporting: true, problem: "clock-skew:+88")));
    }

    [Fact]
    public void Pending_is_not_a_fleet_member_yet()
    {
        Assert.Equal(DeviceState.Pending, DeviceLiveness.Of(Device(online: true, reporting: true, status: "pending")));
    }

    [Fact]
    public void Connected_outranks_a_flaky_history()
    {
        Assert.Equal(DeviceState.Online, DeviceLiveness.Of(Device(online: true, reporting: true, reconnects: 10)));
    }

    [Fact]
    public void A_machine_that_stopped_reporting_is_offline_whatever_its_link_did()
    {
        Assert.Equal(DeviceState.Offline, DeviceLiveness.Of(Device(online: false, reporting: false, reconnects: 10)));
    }

    [Fact]
    public void Reporting_over_a_churning_link_is_flaky()
    {
        Assert.Equal(DeviceState.Flaky, DeviceLiveness.Of(Device(reporting: true, reconnects: DeviceInfo.FlakyReconnectThreshold)));
    }

    [Fact]
    public void Reporting_without_a_command_channel_is_reporting_not_offline()
    {
        Assert.Equal(DeviceState.Reporting, DeviceLiveness.Of(Device(reporting: true, reconnects: DeviceInfo.FlakyReconnectThreshold - 1)));
    }

    [Fact]
    public void Problem_text_spells_out_the_skew_and_shows_unknown_codes_raw()
    {
        var ahead = DeviceLiveness.ProblemText(Device(problem: "clock-skew:+88"));
        var behind = DeviceLiveness.ProblemText(Device(problem: "clock-skew:-34"));
        var unknown = DeviceLiveness.ProblemText(Device(problem: "disk-full:C"));

        Assert.Contains("88", ahead);
        Assert.Contains("34", behind);
        Assert.NotEqual(ahead, behind);
        Assert.Contains("disk-full:C", unknown);
        Assert.Equal("", DeviceLiveness.ProblemText(Device()));
    }

    [Fact]
    public void Every_state_has_a_label()
    {
        foreach (var state in Enum.GetValues<DeviceState>())
            Assert.False(string.IsNullOrWhiteSpace(DeviceLiveness.Label(state)), state.ToString());
    }
}
