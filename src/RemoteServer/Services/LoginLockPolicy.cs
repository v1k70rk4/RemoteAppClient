namespace RemoteServer.Services;

/// <summary>
/// How long a sign-in lock lasts. A device's lock used to hold until an administrator cleared it, and the device
/// id in a sign-in request is the caller's word (the admin API arrives through the tunnel, not under the device's
/// own certificate), so anyone who knew a device id could lock that device's console out with five bad passwords
/// and keep it out. The lock now runs out on its own, like the keyless operators' source-IP lock; it still slows
/// a password guess to five tries an hour and still alerts.
/// </summary>
public static class LoginLockPolicy
{
    public const int LockMinutes = 60;

    /// <summary>True while a lock set at <paramref name="lockedAt"/> still holds at <paramref name="now"/>.</summary>
    public static bool IsActive(DateTimeOffset? lockedAt, DateTimeOffset now) =>
        lockedAt is { } t && t.AddMinutes(LockMinutes) > now;
}
