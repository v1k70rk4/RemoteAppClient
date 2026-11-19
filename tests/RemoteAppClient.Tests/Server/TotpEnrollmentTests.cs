using OtpNet;
using RemoteServer.Security;

namespace RemoteAppClient.Tests.Server;

/// <summary>
/// Setting up an authenticator: a sign-in before the first confirmation hands out the secret already on
/// record, so a phone that scanned it earlier keeps producing accepted codes; and the first confirmation
/// takes a code about a minute old, sign-in does not.
/// </summary>
public class TotpEnrollmentTests
{
    // A fixed moment a few seconds into a 30 s step, so "60 s ago" is exactly two steps back.
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 5, DateTimeKind.Utc);

    [Fact]
    public void The_secret_on_record_is_handed_out_again()
    {
        var stored = TotpService.GenerateSecret();
        var secret = TotpService.PendingSecret(stored, out var isNew);
        Assert.Equal(stored, secret);
        Assert.False(isNew);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base32 at all!")]
    [InlineData("A")]          // decodes to no bytes
    [InlineData("========")]   // padding only
    [InlineData("MFRGGZDF")]   // valid base32, but 5 bytes: too short to be a key
    public void Without_a_usable_secret_a_new_one_is_made(string? stored)
    {
        var secret = TotpService.PendingSecret(stored, out var isNew);
        Assert.True(isNew);
        Assert.NotEqual(stored, secret);
        Assert.Equal(20, Base32Encoding.ToBytes(secret).Length);
    }

    [Fact]
    public void A_code_a_minute_old_confirms_an_authenticator_but_does_not_sign_in()
    {
        var secret = TotpService.GenerateSecret();
        var minuteOld = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(Now.AddSeconds(-60));

        Assert.True(TotpService.VerifyEnrollment(secret, minuteOld, Now, out _));
        Assert.False(TotpService.Verify(secret, minuteOld, Now, out _));
    }

    [Fact]
    public void A_code_ninety_seconds_old_confirms_nothing()
    {
        var secret = TotpService.GenerateSecret();
        var stale = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(Now.AddSeconds(-90));

        Assert.False(TotpService.VerifyEnrollment(secret, stale, Now, out _));
    }

    [Fact]
    public void The_current_code_does_both()
    {
        var secret = TotpService.GenerateSecret();
        var now = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(Now);
        Assert.True(TotpService.VerifyEnrollment(secret, now, Now, out var a));
        Assert.True(TotpService.Verify(secret, now, Now, out var b));
        Assert.Equal(a, b);
    }
}
