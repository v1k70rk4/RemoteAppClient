using System.Collections.Concurrent;

namespace RemoteServer.Security;

/// <summary>
/// Makes an accepted TOTP code single-use. The verifier tolerates one step of clock drift either way, so a code
/// stays valid for up to 90 seconds; without this, a code seen once (shoulder, log, proxy) could sign in again
/// inside that window. Per user, the newest accepted time step is remembered and anything at or before it is
/// refused. In memory only: a restart forgets at most one 90-second window, and a user can only ever lose a
/// step they have already used.
/// </summary>
public sealed class TotpReplayGuard
{
    private readonly ConcurrentDictionary<Guid, long> _lastStep = new();

    /// <summary>True when <paramref name="step"/> is newer than the last one accepted for the user; records it.</summary>
    public bool TryAccept(Guid userId, long step)
    {
        while (true)
        {
            if (!_lastStep.TryGetValue(userId, out var last))
            {
                if (_lastStep.TryAdd(userId, step)) return true;
                continue;
            }
            if (step <= last) return false;
            if (_lastStep.TryUpdate(userId, step, last)) return true;
        }
    }
}
