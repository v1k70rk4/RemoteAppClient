using RemoteServer.Security;

namespace RemoteAppClient.Tests.Server;

/// <summary>An accepted authenticator code is used up: the same code must not sign in twice within its window.</summary>
public class TotpReplayGuardTests
{
    [Fact]
    public void A_code_is_accepted_once()
    {
        var guard = new TotpReplayGuard();
        var user = Guid.NewGuid();
        Assert.True(guard.TryAccept(user, 1000));
        Assert.False(guard.TryAccept(user, 1000));
    }

    [Fact]
    public void An_older_step_is_refused_after_a_newer_one()
    {
        var guard = new TotpReplayGuard();
        var user = Guid.NewGuid();
        Assert.True(guard.TryAccept(user, 1001));
        Assert.False(guard.TryAccept(user, 1000)); // the "previous" step of the ±1 window, already behind us
        Assert.True(guard.TryAccept(user, 1002));
    }

    [Fact]
    public void Users_do_not_share_a_history()
    {
        var guard = new TotpReplayGuard();
        Assert.True(guard.TryAccept(Guid.NewGuid(), 1000));
        Assert.True(guard.TryAccept(Guid.NewGuid(), 1000));
    }

    [Fact]
    public void Verify_reports_the_matched_step()
    {
        var secret = TotpService.GenerateSecret();
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        Assert.True(TotpService.Verify(secret, code, out var step));
        Assert.True(step > 0);
        Assert.False(TotpService.Verify(secret, "000000", out _) && TotpService.Verify(secret, "123456", out _)); // at most one of two guesses can be right
    }

    [Fact]
    public void Parallel_use_of_one_code_lets_exactly_one_through()
    {
        var guard = new TotpReplayGuard();
        var user = Guid.NewGuid();
        var accepted = 0;
        Parallel.For(0, 64, _ => { if (guard.TryAccept(user, 5000)) Interlocked.Increment(ref accepted); });
        Assert.Equal(1, accepted);
    }
}
