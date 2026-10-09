using System.Security.Cryptography;
using RemoteAgent.Commands;

namespace RemoteAppClient.Tests.Contracts;

/// <summary>
/// The version 2 form covers every field and the device the command is for; the version 1 form stays what the
/// agents in the field verify. A command names its version, and the verifier checks that one.
/// </summary>
public class CommandSignatureV2Tests
{
    private static AgentCommand Cmd(string type, CommandData? data = null) =>
        new() { Type = type, Nonce = "n-1", IssuedAt = 1_700_000_000, Data = data };

    [Fact]
    public void V2_form_is_length_prefixed_and_starts_with_the_device()
    {
        var c = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 50123, ConsentRequired = true, FileRemotePort = 60123, FileToken = "t;k", TunnelPurpose = "file" });
        var s = CommandSignature.Canonicalize(c, "dev-1");
        Assert.StartsWith("v2;5:dev-1;11:open-tunnel;3:n-1;10:1700000000;5:50123;-;-;-;-;1:1;-;", s);
        Assert.EndsWith("5:60123;3:t;k;4:file;", s);
    }

    [Fact]
    public void V2_form_distinguishes_missing_from_empty()
    {
        var missing = CommandSignature.Canonicalize(Cmd(CommandTypes.Message, new CommandData { MessageKind = "text" }), "d");
        var empty = CommandSignature.Canonicalize(Cmd(CommandTypes.Message, new CommandData { MessageKind = "text", MessageText = "" }), "d");
        Assert.NotEqual(missing, empty);
    }

    [Fact]
    public void A_value_containing_the_separator_cannot_shift_the_fields()
    {
        var a = CommandSignature.Canonicalize(Cmd(CommandTypes.Message, new CommandData { MessageFrom = "op;4:text", MessageText = "x" }), "d");
        var b = CommandSignature.Canonicalize(Cmd(CommandTypes.Message, new CommandData { MessageFrom = "op", MessageText = "4:text;1:x" }), "d");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Signing_with_a_device_marks_version_2_and_binds_the_device()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 1, FileToken = "tok", TunnelPurpose = "vnc" });
        CommandSignature.Sign(c, key, "dev-1");

        Assert.Equal(2, c.SigVersion);
        Assert.True(CommandSignature.Verify(c, key, "dev-1"));
        Assert.False(CommandSignature.Verify(c, key, "dev-2"));   // captured for another device
        Assert.False(CommandSignature.Verify(c, key));            // a verifier without a device id cannot accept v2
        Assert.False(CommandSignature.Verify(c, key, ""));
    }

    [Fact]
    public void Version_2_notices_the_fields_version_1_left_unsigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 1, FileRemotePort = 60001, FileToken = "tok", TunnelPurpose = "vnc" });
        CommandSignature.Sign(c, key, "dev-1");

        c.Data!.TunnelPurpose = "file";
        Assert.False(CommandSignature.Verify(c, key, "dev-1"));
        c.Data.TunnelPurpose = "vnc";
        c.Data.FileToken = "other";
        Assert.False(CommandSignature.Verify(c, key, "dev-1"));
        c.Data.FileToken = "tok";
        Assert.True(CommandSignature.Verify(c, key, "dev-1"));
    }

    [Fact]
    public void Signing_without_a_device_stays_version_1_and_old_verifiers_accept_it()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.Ping);
        CommandSignature.Sign(c, key);
        Assert.Equal(0, c.SigVersion);
        Assert.True(CommandSignature.Verify(c, key));          // an agent that knows no device id (pre-2.2.7 code path)
        Assert.True(CommandSignature.Verify(c, key, "dev-1"));  // a new agent still accepts the original form
    }

    [Fact]
    public void A_version_this_build_does_not_know_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.Ping);
        CommandSignature.Sign(c, key, "dev-1");
        c.SigVersion = 3;
        Assert.False(CommandSignature.Verify(c, key, "dev-1"));
    }

    [Fact]
    public void Relabelling_a_v1_signature_as_v2_fails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.Ping);
        CommandSignature.Sign(c, key);
        c.SigVersion = 2;
        Assert.False(CommandSignature.Verify(c, key, "dev-1"));
    }
}
