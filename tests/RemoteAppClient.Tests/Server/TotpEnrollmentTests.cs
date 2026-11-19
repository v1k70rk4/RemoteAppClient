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
    public void Without_a_readable_secret_a_new_one_is_made(string? stored)
    {
        var secret = TotpService.PendingSecret(stored, out var isNew);
        Assert.True(isNew);
        Assert.NotEqual(stored, secret);
        Assert.Equal(20, Base32Encoding.ToBytes(secret).Length);
    }

    [Fact]
    public void A_code_a_minute_old_confirms_an_authenticator_but_does_not_sign_in()
    {
        // Two steps back is exact only while the clock stays inside the current 30 s step: start fresh.
        if (DateTime.UtcNow.Second % 30 >= 27) Thread.Sleep(TimeSpan.FromSeconds(4));
        var secret = TotpService.GenerateSecret();
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var minuteOld = totp.ComputeTotp(DateTime.UtcNow.AddSeconds(-60));

        Assert.True(TotpService.VerifyEnrollment(secret, minuteOld, out _));
        Assert.False(TotpService.Verify(secret, minuteOld, out _));
    }

    [Fact]
    public void A_code_ninety_seconds_old_confirms_nothing()
    {
        if (DateTime.UtcNow.Second % 30 >= 27) Thread.Sleep(TimeSpan.FromSeconds(4));
        var secret = TotpService.GenerateSecret();
        var stale = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow.AddSeconds(-90));

        Assert.False(TotpService.VerifyEnrollment(secret, stale, out _));
    }

    [Fact]
    public void The_current_code_does_both()
    {
        var secret = TotpService.GenerateSecret();
        var now = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp();
        Assert.True(TotpService.VerifyEnrollment(secret, now, out var a));
        Assert.True(TotpService.Verify(secret, now, out var b));
        Assert.Equal(a, b);
    }
}
