using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Admin;
using RemoteAgent.Commands;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Files;

/// <summary>
/// Loopback HTTP file service backing the operator's Total Commander-style remote file manager. Runs as
/// SYSTEM (full filesystem), reachable ONLY through the authenticated reverse tunnel and the per-session
/// token. The reverse tunnel forwards a bastion port to 127.0.0.1:<see cref="Port"/>. Every request must
/// carry the matching X-File-Token; operations are logged for audit.
/// </summary>
public sealed class FileService(ILogger logger) : IAsyncDisposable
{
    public const int Port = 5901;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    // Session token -> the lock stamp (FileLock.LockStamp) when it was issued. A stamp that later differs
    // means the lock was switched on since, whether or not it is still on; it is kept per token, so a token
    // issued after a lock cycle cannot vouch for one issued before it.
    private readonly Dictionary<string, long> _tokens = new();
    private readonly object _lock = new();
    // Cancelled whenever the tokens are cleared, so a transfer running on a token that just died stops where it
    // is instead of finishing its copy. Replaced after each cancellation; a request captures the current one.
    private CancellationTokenSource _revoke = new();
    // Requests being served. A read or write pending on the peer does not notice the cancellation (the listener's
    // streams check it only before an operation starts), so revoking also aborts these contexts.
    private readonly HashSet<HttpListenerContext> _active = new();

    /// <summary>How often the local lock is looked at while the service is up, so switching it on at the device
    /// ends the session even when no request arrives.</summary>
    private static readonly TimeSpan LockPoll = TimeSpan.FromSeconds(5);

    /// <summary>Forgets every session token: the tunnel is closed, so nothing may use the service until a new
    /// open-tunnel brings a new token. The listener stays up, so the port is never free for someone else. A
    /// transfer in flight on one of the forgotten tokens is cut off.</summary>
    public void ClearTokens() => ClearTokens(except: null);

    /// <summary>As <see cref="ClearTokens()"/>; <paramref name="except"/> is the request that found the lock and
    /// still has a 403 to send, so it is not aborted with the rest.</summary>
    private void ClearTokens(HttpListenerContext? except)
    {
        CancellationTokenSource old;
        HttpListenerContext[] active;
        lock (_lock)
        {
            _tokens.Clear();
            old = _revoke;
            _revoke = new CancellationTokenSource();
            if (except is not null) _active.Remove(except);
            active = _active.ToArray();
        }
        try { old.Cancel(); } catch { /* best effort */ }
        old.Dispose();
        foreach (var ctx in active)
            try { ctx.Response.Abort(); } catch { /* best effort: the peer may be gone already */ }
    }

    /// <summary>True when the person at the device has locked file transfer since a token was issued: the flag
    /// is on, or the stamp a lock leaves differs from the one seen when the token was issued (switched on and
    /// off again in between). Without a token (the watcher) every token is checked.</summary>
    private bool LockedSinceIssue(string? token = null)
    {
        if (FileLock.IsLocked()) return true;
        var stamp = FileLock.LockStamp();
        lock (_lock)
            return token is null ? _tokens.Values.Any(s => s != stamp)
                                 : _tokens.TryGetValue(token, out var s) && s != stamp;
    }

    /// <summary>Starts the service and registers a session token (an empty one only binds the port, which is
    /// done at agent start so no local user can take it first). Concurrent operators each add their own,
    /// so a second operator joining does not invalidate the first. Idempotent on the listener.</summary>
    public void Start(string token)
    {
        if (!string.IsNullOrEmpty(token))
        {
            // The tunnel was opened under the lock as it stands now; a lock from here on ends this token. A
            // token issued before a lock cycle is revoked first, rather than living on beside the new one.
            if (LockedSinceIssue()) ClearTokens();
            lock (_lock) _tokens[token] = FileLock.LockStamp();
        }
        if (_listener is { IsListening: true }) return;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { _listener.Start(); }
        catch (Exception ex) { logger.LogWarning(ex, "File service could not bind 127.0.0.1:{Port}.", Port); return; }
        _ = AcceptLoopAsync(_cts.Token);
        _ = WatchLockAsync(_cts.Token);
        logger.LogInformation("File service listening on 127.0.0.1:{Port}.", Port);
    }

    /// <summary>Ends the session the moment the person at the device locks file transfer, whether or not a
    /// request comes in: the lock is a registry flag with no event, so it is polled while the service is up. A
    /// lock switched on and off again between two polls is caught by its stamp.</summary>
    private async Task WatchLockAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LockPoll, ct);
                bool live;
                lock (_lock) live = _tokens.Count > 0;
                if (live && LockedSinceIssue())
                {
                    ClearTokens();
                    logger.LogWarning(L.FileService_LockedRequestRefused);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var tok = req.Headers["X-File-Token"];
            bool authorized;
            CancellationToken revoke;
            lock (_lock)
            {
                authorized = tok != null && _tokens.ContainsKey(tok);
                revoke = _revoke.Token;
                if (authorized) _active.Add(ctx);
            }
            if (!authorized) { ctx.Response.StatusCode = 403; ctx.Response.Close(); return; }
            // The local lock used to count only when a tunnel was opened: a session already running kept its
            // token and went on reading, writing and deleting as SYSTEM after the person at the device had
            // disabled file transfer. It is checked on every request now, and switching it on ends the session.
            if (LockedSinceIssue(tok))
            {
                ClearTokens(except: ctx);   // the others are cut off; this one still answers 403
                logger.LogWarning(L.FileService_LockedRequestRefused);
                ctx.Response.StatusCode = 403; ctx.Response.Close(); return;
            }

            var path = req.QueryString["path"] ?? "";
            switch (req.Url!.AbsolutePath.ToLowerInvariant())
            {
                case "/drives": await WriteJsonAsync(ctx, Drives()); break;
                case "/home": await WriteJsonAsync(ctx, ListDir(HomeDir())); break;
                case "/list": await WriteJsonAsync(ctx, ListDir(path)); break;
                case "/download": await DownloadAsync(ctx, path, revoke); break;
                case "/upload": await UploadAsync(ctx, path, revoke); break;
                case "/mkdir": Directory.CreateDirectory(path); Audit("mkdir", path); Ok(ctx); break;
                case "/delete": DeleteEntry(path); Audit("delete", path); Ok(ctx); break;
                case "/rename": RenameEntry(path, req.QueryString["to"] ?? ""); Audit("rename", $"{path} -> {req.QueryString["to"]}"); Ok(ctx); break;
                default: ctx.Response.StatusCode = 404; ctx.Response.Close(); break;
            }
        }
        catch (Exception ex)
        {
            if (revoked(ctx)) return;   // cut off by ClearTokens: the context is aborted, nothing to answer
            // Log the detail server-side; return a generic, non-reflective body so no request-controlled
            // data is echoed back into the response.
            logger.LogWarning(ex, "File service request failed.");
            try { ctx.Response.StatusCode = 500; ctx.Response.ContentType = "text/plain; charset=utf-8"; var b = Encoding.UTF8.GetBytes("Operation failed."); ctx.Response.OutputStream.Write(b); ctx.Response.Close(); } catch { /* client gone */ }
        }
        finally
        {
            lock (_lock) _active.Remove(ctx);
        }

        // A context ClearTokens aborted is no longer in the active set by the time its handler fails.
        bool revoked(HttpListenerContext c) { lock (_lock) return !_active.Contains(c); }
    }

    private static FsList Drives()
    {
        var list = new FsList { Path = "" };
        foreach (var d in DriveInfo.GetDrives())
            if (d.IsReady) list.Entries.Add(new FsEntry { Name = d.Name, IsDir = true });
        return list;
    }

    private static FsList ListDir(string path)
    {
        var list = new FsList { Path = path };
        var di = new DirectoryInfo(path);
        foreach (var dir in di.GetDirectories())
            try { list.Entries.Add(new FsEntry { Name = dir.Name, IsDir = true, Modified = dir.LastWriteTimeUtc }); } catch { /* skip inaccessible */ }
        foreach (var f in di.GetFiles())
            try { list.Entries.Add(new FsEntry { Name = f.Name, Size = f.Length, Modified = f.LastWriteTimeUtc }); } catch { /* skip inaccessible */ }
        return list;
    }

    private async Task DownloadAsync(HttpListenerContext ctx, string path, CancellationToken revoke)
    {
        ctx.Response.ContentType = "application/octet-stream";
        await using var fs = File.OpenRead(path);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream, revoke);   // cut off when the session's tokens die
        ctx.Response.Close();
        Audit("download", path);
    }

    private async Task UploadAsync(HttpListenerContext ctx, string path, CancellationToken revoke)
    {
        // The body lands in a part file next to the destination and replaces it only once it is complete: a
        // transfer cut off (revoked, or the peer gone) leaves the destination as it was, and the part file is
        // removed. An existing destination lends the part file its access rules for the duration (so the body
        // is no more readable than the file it replaces) and keeps them through the swap (ReplaceFile carries
        // the replaced file's DACL over); the upload fails rather than proceed if the rules cannot be copied.
        var part = path + ".part-" + Guid.NewGuid().ToString("N");
        try
        {
            bool existing = File.Exists(path);
            await using (var fs = File.Create(part))
            {
                if (existing) new FileInfo(part).SetAccessControl(new FileInfo(path).GetAccessControl());
                await ctx.Request.InputStream.CopyToAsync(fs, revoke);   // cut off when the session's tokens die
            }
            revoke.ThrowIfCancellationRequested();   // revoked while the body was landing: the destination stays
            if (existing) File.Replace(part, path, destinationBackupFileName: null);
            else File.Move(part, path);
        }
        catch
        {
            try { File.Delete(part); } catch { /* best effort */ }
            throw;
        }
        Audit("upload", path);
        Ok(ctx);
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }

    private static void RenameEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to, overwrite: false);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, FsList list)
    {
        ctx.Response.ContentType = "application/json";
        var json = JsonSerializer.SerializeToUtf8Bytes(list, AgentJsonContext.Default.FsList);
        ctx.Response.ContentLength64 = json.Length;
        await ctx.Response.OutputStream.WriteAsync(json);
        ctx.Response.Close();
    }

    private static void Ok(HttpListenerContext ctx) { ctx.Response.StatusCode = 200; ctx.Response.Close(); }

    // Strip CR/LF from the request-controlled detail so a crafted path cannot forge extra audit-log lines.
    private void Audit(string op, string detail) => logger.LogInformation("file {Op}: {Detail}", op, detail.Replace('\r', ' ').Replace('\n', ' '));

    /// <summary>Logged-in user's home (best-effort): the system drive's Users\&lt;active console user&gt;, else the drive root.</summary>
    private static string HomeDir()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        try
        {
            uint session = WTSGetActiveConsoleSessionId();
            if (session != 0xFFFFFFFF &&
                WTSQuerySessionInformation(IntPtr.Zero, session, 5 /* WTSUserName */, out IntPtr buf, out _))
            {
                var user = Marshal.PtrToStringUni(buf);
                WTSFreeMemory(buf);
                if (!string.IsNullOrWhiteSpace(user))
                {
                    var home = Path.Combine(root, "Users", user!);
                    if (Directory.Exists(home)) return home;
                }
            }
        }
        catch { /* fall back */ }
        return root;
    }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytesReturned);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { /* best effort */ }
        ClearTokens();
        try { _listener?.Stop(); _listener?.Close(); } catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
