using RemoteAgent.Admin;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>Devices sharing a hostname are numbered by enrolment: the older record is 1.</summary>
public class DuplicateNamesTests
{
    private static DeviceInfo Dev(string id, string host, DateTimeOffset? enrolled = null, DateTimeOffset? seen = null) =>
        new() { DeviceId = id, Hostname = host, EnrolledAt = enrolled, LastSeenAt = seen };

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_older_record_is_1_and_the_re_enrolled_one_is_2()
    {
        var n = DuplicateNames.Number([Dev("new", "EBH-1", T0.AddDays(10)), Dev("old", "EBH-1", T0), Dev("x", "EBH-2", T0)]);
        Assert.Equal((1, 2), n["old"]);
        Assert.Equal((2, 2), n["new"]);
        Assert.False(n.ContainsKey("x"));
    }

    [Fact]
    public void Names_match_regardless_of_case_and_spacing()
    {
        var n = DuplicateNames.Number([Dev("a", "ebh-1", T0), Dev("b", "EBH-1 ", T0.AddHours(1))]);
        Assert.Equal((1, 2), n["a"]);
        Assert.Equal((2, 2), n["b"]);
    }

    [Fact]
    public void Without_an_enrolment_time_the_last_report_orders_them()
    {
        var n = DuplicateNames.Number([Dev("a", "EBH-1", seen: T0.AddDays(3)), Dev("b", "EBH-1", seen: T0)]);
        Assert.Equal((2, 2), n["a"]);
        Assert.Equal((1, 2), n["b"]);
    }

    [Fact]
    public void Unnamed_devices_are_not_grouped()
    {
        var n = DuplicateNames.Number([Dev("a", "", T0), Dev("b", "", T0)]);
        Assert.Empty(n);
    }
}
