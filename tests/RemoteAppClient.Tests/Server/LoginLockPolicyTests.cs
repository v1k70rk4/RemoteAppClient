using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>A device's sign-in lock holds for an hour and then runs out on its own.</summary>
public class LoginLockPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_lock_is_not_active() => Assert.False(LoginLockPolicy.IsActive(null, T0));

    [Fact]
    public void A_fresh_lock_holds_until_the_hour_is_up()
    {
        Assert.True(LoginLockPolicy.IsActive(T0, T0));
        Assert.True(LoginLockPolicy.IsActive(T0, T0.AddMinutes(LoginLockPolicy.LockMinutes - 1)));
        Assert.False(LoginLockPolicy.IsActive(T0, T0.AddMinutes(LoginLockPolicy.LockMinutes)));
        Assert.False(LoginLockPolicy.IsActive(T0, T0.AddDays(3)));
    }
}
