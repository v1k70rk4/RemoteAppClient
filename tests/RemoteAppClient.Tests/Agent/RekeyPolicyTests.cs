using RemoteAgent.Security;

namespace RemoteAppClient.Tests.Agent;

/// <summary>When a device asks for a new key: into the TPM as soon as it can, and before its certificate ends.</summary>
public class RekeyPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("file", true, "tpm")]
    [InlineData("software", true, "tpm")]
    [InlineData("tpm", true, null)]
    [InlineData("file", false, null)]
    [InlineData("software", false, null)]
    public void Moves_into_the_tpm_when_it_is_usable_and_the_key_is_elsewhere(string provider, bool tpmUsable, string? expected) =>
        Assert.Equal(expected, RekeyPolicy.Reason(provider, tpmUsable, Now.AddDays(400), Now));

    [Fact]
    public void Renews_inside_the_last_sixty_days()
    {
        Assert.Equal("renewal", RekeyPolicy.Reason("tpm", true, Now.AddDays(59), Now));
        Assert.Equal("renewal", RekeyPolicy.Reason("tpm", true, Now.AddDays(-1), Now)); // already expired: still try
        Assert.Null(RekeyPolicy.Reason("tpm", true, Now.AddDays(61), Now));
        Assert.Null(RekeyPolicy.Reason("tpm", true, null, Now)); // unknown expiry: nothing to act on
    }

    [Fact]
    public void The_tpm_move_wins_over_renewal_since_it_renews_too() =>
        Assert.Equal("tpm", RekeyPolicy.Reason("file", true, Now.AddDays(10), Now));
}
