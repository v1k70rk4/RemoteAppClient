using System.Security.Cryptography;
using RemoteAgent.Commands;

namespace RemoteAppClient.Tests.Contracts;

/// <summary>
/// The canonical form is the one thing the server and every agent in the field must agree on. A field that
/// is appended for one command type must stay out of every other type's form, or agents that predate the
/// field would reject commands they used to accept.
/// </summary>
public class CommandSignatureTests
{
    private static AgentCommand Cmd(string type, CommandData? data = null) =>
        new() { Type = type, Nonce = "n-1", IssuedAt = 1_700_000_000, Data = data };

    [Fact]
    public void OpenTunnel_form_carries_the_port_and_the_access_policy_only()
    {
        var c = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 50123, ConsentRequired = true, UnattendedAllowed = false });
        Assert.Equal("open-tunnel|n-1|1700000000|50123||||" + "|True|False", CommandSignature.Canonicalize(c));
    }

    [Fact]
    public void Missing_data_canonicalizes_to_the_legacy_defaults()
    {
        var c = Cmd(CommandTypes.Ping);
        Assert.Equal("ping|n-1|1700000000|0|||||False|True", CommandSignature.Canonicalize(c));
    }

    [Fact]
    public void Auxiliary_tunnel_fields_are_not_signed()
    {
        var plain = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 1 });
        var aux = Cmd(CommandTypes.OpenTunnel, new CommandData { RemotePort = 1, FileRemotePort = 60001, FileToken = "t", TunnelPurpose = "file" });
        Assert.Equal(CommandSignature.Canonicalize(plain), CommandSignature.Canonicalize(aux));
    }

    [Theory]
    [InlineData(CommandTypes.Message)]
    [InlineData(CommandTypes.Power)]
    [InlineData(CommandTypes.Diag)]
    public void Type_specific_fields_are_appended_only_for_their_own_type(string type)
    {
        var data = new CommandData { MessageKind = "text", MessageFrom = "op", MessageText = "hi", PowerAction = "restart", DiagHours = 24 };
        var own = CommandSignature.Canonicalize(Cmd(type, data));
        var other = CommandSignature.Canonicalize(Cmd(CommandTypes.OpenTunnel, data));

        Assert.Equal("open-tunnel|n-1|1700000000|0|||||False|True", other);
        Assert.EndsWith(type switch
        {
            CommandTypes.Message => "|text|op|hi",
            CommandTypes.Power => "|restart",
            _ => "|24",
        }, own);
        Assert.StartsWith(type + "|n-1|1700000000|0|||||False|True", own);
    }

    [Fact]
    public void Diag_hours_default_to_zero_and_zero_differs_from_a_day()
    {
        Assert.EndsWith("|0", CommandSignature.Canonicalize(Cmd(CommandTypes.Diag, new CommandData())));
        Assert.NotEqual(
            CommandSignature.Canonicalize(Cmd(CommandTypes.Diag, new CommandData { DiagHours = 0 })),
            CommandSignature.Canonicalize(Cmd(CommandTypes.Diag, new CommandData { DiagHours = 24 })));
    }

    [Fact]
    public void Signature_round_trips_and_notices_every_signed_field()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.Diag, new CommandData { DiagHours = 24 });
        CommandSignature.Sign(c, key);
        Assert.True(CommandSignature.Verify(c, key));

        c.Data!.DiagHours = 72;
        Assert.False(CommandSignature.Verify(c, key));
        c.Data.DiagHours = 24;
        Assert.True(CommandSignature.Verify(c, key));

        c.Nonce = "n-2";
        Assert.False(CommandSignature.Verify(c, key));
        c.Nonce = "n-1";
        c.IssuedAt++;
        Assert.False(CommandSignature.Verify(c, key));
    }

    [Fact]
    public void Another_key_or_a_broken_signature_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var c = Cmd(CommandTypes.Power, new CommandData { PowerAction = "restart" });
        CommandSignature.Sign(c, key);

        Assert.False(CommandSignature.Verify(c, other));
        c.Signature = "not base64!";
        Assert.False(CommandSignature.Verify(c, key));
        c.Signature = "";
        Assert.False(CommandSignature.Verify(c, key));
    }
}
