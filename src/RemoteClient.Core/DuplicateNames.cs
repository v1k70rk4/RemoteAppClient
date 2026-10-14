using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>
/// Numbers the devices that share a hostname. A machine re-imaged by Autopilot enrols as a new device (new
/// identity, new key) under its old name, so the fleet shows the name twice until the old record is removed;
/// the number tells the records apart. Ordered by enrolment (the older record is 1), falling back to the last
/// report when an older server does not send the enrolment time.
/// </summary>
public static class DuplicateNames
{
    /// <summary>For every device whose hostname another device shares: its ordinal (1 = enrolled first) and how
    /// many carry the name. Devices with a unique name, or no name, are absent.</summary>
    public static Dictionary<string, (int N, int Of)> Number(IEnumerable<DeviceInfo> devices)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        foreach (var group in devices.Where(d => !string.IsNullOrWhiteSpace(d.Hostname))
                     .GroupBy(d => d.Hostname.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = group.OrderBy(d => d.EnrolledAt ?? d.LastSeenAt ?? DateTimeOffset.MinValue)
                               .ThenBy(d => d.DeviceId, StringComparer.Ordinal).ToList();
            if (ordered.Count < 2) continue;
            for (int i = 0; i < ordered.Count; i++) result[ordered[i].DeviceId] = (i + 1, ordered.Count);
        }
        return result;
    }
}
