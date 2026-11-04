using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using RemoteAgent.Commands;
using RemoteServer.Configuration;
using RemoteServer.Signing;

namespace RemoteAppClient.Tests.Server;

/// <summary>A queued command keeps its nonce through every delivery attempt: the console waits on the nonce it was
/// given at the request, and a sleeping device's answer at its wake must arrive under that one.</summary>
public class CommandSignerNonceTests
{
    private static CommandSigner Signer(out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = Path.Combine(Path.GetTempPath(), "rac-test-" + Guid.NewGuid().ToString("N") + ".pem");
        File.WriteAllText(path, key.ExportECPrivateKeyPem());
        try { return new CommandSigner(Options.Create(new ServerOptions { CommandSigningKeyPath = path })); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_redelivery_keeps_the_nonce_and_signs_afresh()
    {
        using var signer = Signer(out var key);
        var first = signer.Create(CommandTypes.OpenTunnel, new CommandData { RemotePort = 50001 }, "dev");
        var again = signer.Create(CommandTypes.OpenTunnel, new CommandData { RemotePort = 50001 }, "dev", nonce: first.Nonce);
        Assert.Equal(first.Nonce, again.Nonce);
        Assert.True(CommandSignature.Verify(again, key, "dev"));
    }

    [Fact]
    public void Without_a_nonce_each_command_gets_its_own()
    {
        using var signer = Signer(out _);
        var a = signer.Create(CommandTypes.OpenTunnel, null, "dev");
        var b = signer.Create(CommandTypes.OpenTunnel, null, "dev", nonce: "");
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEmpty(b.Nonce);
    }
}
