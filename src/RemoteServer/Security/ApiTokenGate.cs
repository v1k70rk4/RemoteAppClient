using System.Collections.Concurrent;

namespace RemoteServer.Security;

/// <summary>
/// What an access token may reach, and a brake on guessing one.
///
/// Two scopes. <see cref="ScopeRead"/> ("diag") is the default: the server's log, the health snapshot and
/// the fleet listings. <see cref="ScopeUpdate"/> adds the three server self-update routes - stage a
/// package, apply, roll back - and nothing else; it exists so a build machine can ship a server without a
/// console session, and it is deliberately loud in the UI because a package the helper installs IS the
/// server. Both allowlists are exact routes rather than "every GET under /admin": the admin API has GETs
/// that hand out secrets (a device's VNC password, the encrypted fleet backup, generated MSIs with
/// bootstrap blobs, enrollment tokens), and a single-factor credential must never open those.
/// </summary>
public static class ApiTokenGate
{
    public const string ScopeRead = "diag";
    public const string ScopeUpdate = "update";

    public static bool IsKnownScope(string? scope) => scope is ScopeRead or ScopeUpdate;

    private const string DevicesPrefix = "/admin/devices/";
    private const string EventsSuffix = "/events";

    public static bool Allows(string scope, string method, PathString path)
    {
        if (!IsKnownScope(scope)) return false;
        var p = path.Value ?? "";

        if (scope == ScopeUpdate && HttpMethods.IsPost(method))
            return p is "/admin/server/package" or "/admin/server/update" or "/admin/server/rollback";

        if (!HttpMethods.IsGet(method)) return false;
        if (p is "/admin/server/logs" or "/admin/server/diag" or "/admin/server/status"
            or "/admin/devices" or "/admin/devices/online"
            or "/admin/groups" or "/admin/channels" or "/admin/audit")
            return true;

        // /admin/devices/{id}/events only - vnc-secret lives one segment over and is a GET too.
        if (p.StartsWith(DevicesPrefix, StringComparison.Ordinal) && p.EndsWith(EventsSuffix, StringComparison.Ordinal)
            && p.Length > DevicesPrefix.Length + EventsSuffix.Length)
        {
            var id = p.AsSpan(DevicesPrefix.Length, p.Length - DevicesPrefix.Length - EventsSuffix.Length);
            return !id.Contains('/');
        }
        return false;
    }

    // ---- failure brake: after too many rejected tokens from one address, refuse token auth for a while.
    // The /admin path is only reachable through the device tunnel, so this is belt and braces on top of
    // a 256-bit secret, not the primary defence; sessions are unaffected.

    private const int Limit = 10;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Block = TimeSpan.FromMinutes(10);

    private sealed class Fails { public int Count; public DateTimeOffset WindowStart; public DateTimeOffset? BlockedUntil; }
    private static readonly ConcurrentDictionary<string, Fails> ByIp = new();

    public static bool IsBlocked(string ip) =>
        ByIp.TryGetValue(ip, out var f) && f.BlockedUntil is { } until && until > DateTimeOffset.UtcNow;

    public static void RecordFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        var f = ByIp.GetOrAdd(ip, _ => new Fails { WindowStart = now });
        lock (f)
        {
            if (now - f.WindowStart > Window) { f.Count = 0; f.WindowStart = now; }
            f.Count++;
            if (f.Count >= Limit) { f.BlockedUntil = now + Block; f.Count = 0; f.WindowStart = now; }
        }
        // Keep the table small: forget addresses whose window and block are both over.
        if (ByIp.Count > 1000)
            foreach (var kv in ByIp)
                if (now - kv.Value.WindowStart > Window && (kv.Value.BlockedUntil is null || kv.Value.BlockedUntil < now))
                    ByIp.TryRemove(kv.Key, out _);
    }
}
