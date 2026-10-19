using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>Which signature form a device gets: the device-bound one from 2.2.7.1 on, and for a device whose
/// version is not known yet (a fresh install always runs an agent that checks the binding).</summary>
public class CommandSignerPolicyTests
{
    [Theory]
    [InlineData("2.2.7.1", true)]
    [InlineData("3.0.0.1", true)]
    [InlineData("2.2.6.0", false)]
    [InlineData("2.2.0.0", false)]
    public void Agents_from_2_2_7_1_get_the_bound_form(string version, bool bound) =>
        Assert.Equal(bound, CommandSigner.UsesV2(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void A_device_without_a_known_version_gets_the_bound_form(string? version) =>
        Assert.True(CommandSigner.UsesV2(version));
}
