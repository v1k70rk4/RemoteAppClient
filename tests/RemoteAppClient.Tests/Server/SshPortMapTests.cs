using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>The port map the bastion's sshd reads: one line per device, its VNC port and the file port derived
/// from it, in a stable order.</summary>
public class SshPortMapTests
{
    [Theory]
    [InlineData(50000, 60000)]
    [InlineData(50023, 60023)]
    [InlineData(54999, 64999)]
    [InlineData(55000, 60000)]   // wraps: the file range is half the size of the VNC range
    public void The_file_port_follows_the_vnc_port(int vnc, int file) => Assert.Equal(file, TunnelPorts.FileFor(vnc));

    [Fact]
    public void One_tab_separated_line_per_device_sorted_by_id()
    {
        var text = SshPortMap.Render([("dev-b", 50001), ("dev-a", 50023)]);
        Assert.Equal("dev-a\t50023\t60023\ndev-b\t50001\t60001\n", text);
    }

    [Fact]
    public void No_devices_is_an_empty_file() => Assert.Equal("", SshPortMap.Render([]));
}
