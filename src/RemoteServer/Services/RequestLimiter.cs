using System.Collections.Concurrent;

namespace RemoteServer.Services;

/// <summary>
/// A small in-memory rate limit for the few endpoints that answer without a certificate or a session (the
/// lost-key request and its polling): so many hits per key per window, the rest refused. Single-instance, like
/// the rest of the in-memory state; a restart forgets the counts.
/// </summary>
public sealed class RequestLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();
    private const int MaxKeys = 10_000;

    public bool Allow(string key, int max, TimeSpan window)
    {
        if (_hits.Count > MaxKeys) _hits.Clear(); // a flood of distinct sources must not become a memory leak
        var q = _hits.GetOrAdd(key, static _ => new Queue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > window) q.Dequeue();
            if (q.Count >= max) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
