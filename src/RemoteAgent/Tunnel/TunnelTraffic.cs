using System.Net;
using System.Net.NetworkInformation;

namespace RemoteAgent.Tunnel;

/// <summary>
/// Whether the session is in use right now: a live connection to the VNC server or to the file service on this
/// machine's loopback. The reverse tunnel's ssh opens one for every viewer and every file request it carries, so
/// "someone is connected" is read from the connection table rather than from the open-tunnel commands - those
/// come only when an operator connects, and a long copy or a long VNC session sends none.
/// </summary>
public static class TunnelTraffic
{
    /// <summary>The check on the given connections (the testable part).</summary>
    public static bool InUse(IEnumerable<(IPEndPoint Local, IPEndPoint Remote, TcpState State)> connections, IReadOnlyCollection<int> ports) =>
        connections.Any(c => c.State == TcpState.Established
                             && IPAddress.IsLoopback(c.Local.Address) && IPAddress.IsLoopback(c.Remote.Address)
                             && ports.Contains(c.Remote.Port));

    /// <summary>The check on this machine's TCP connections; false if they cannot be read.</summary>
    public static bool InUse(params int[] ports)
    {
        try
        {
            var table = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
            return InUse(table.Select(c => (c.LocalEndPoint, c.RemoteEndPoint, c.State)), ports);
        }
        catch { return false; }   // not knowing is not "in use": the idle timeout keeps its old meaning
    }
}
