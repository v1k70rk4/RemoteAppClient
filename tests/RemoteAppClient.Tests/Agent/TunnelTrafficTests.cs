using System.Net;
using System.Net.NetworkInformation;
using RemoteAgent.Tunnel;

namespace RemoteAppClient.Tests.Agent;

/// <summary>The idle timeout's "in use": a live loopback connection to the VNC server or the file service.</summary>
public class TunnelTrafficTests
{
    private static readonly int[] Ports = [5900, 5901];

    private static (IPEndPoint, IPEndPoint, TcpState) Conn(string local, int localPort, string remote, int remotePort, TcpState state = TcpState.Established) =>
        (new IPEndPoint(IPAddress.Parse(local), localPort), new IPEndPoint(IPAddress.Parse(remote), remotePort), state);

    [Theory]
    [InlineData(5900)]   // a VNC viewer through the tunnel
    [InlineData(5901)]   // a file transfer through the tunnel
    public void A_live_loopback_connection_to_a_session_port_is_use(int port) =>
        Assert.True(TunnelTraffic.InUse([Conn("127.0.0.1", 50123, "127.0.0.1", port)], Ports));

    [Fact]
    public void The_server_side_of_the_connection_alone_is_not_counted_twice_nor_needed() =>
        // The listener's own entry has the session port as its LOCAL end; the ssh side's entry is what counts.
        Assert.False(TunnelTraffic.InUse([Conn("127.0.0.1", 5900, "127.0.0.1", 50123)], Ports));

    [Fact]
    public void A_connection_that_is_closing_is_not_use() =>
        Assert.False(TunnelTraffic.InUse([Conn("127.0.0.1", 50123, "127.0.0.1", 5900, TcpState.TimeWait)], Ports));

    [Fact]
    public void Another_port_is_not_use() =>
        Assert.False(TunnelTraffic.InUse([Conn("127.0.0.1", 50123, "127.0.0.1", 445)], Ports));

    [Fact]
    public void A_connection_from_the_network_is_not_use() =>
        Assert.False(TunnelTraffic.InUse([Conn("192.168.1.10", 50123, "192.168.1.20", 5900)], Ports));

    [Fact]
    public void No_connections_no_use() =>
        Assert.False(TunnelTraffic.InUse([], Ports));
}
