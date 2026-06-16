using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

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
    private readonly HashSet<string> _tokens = new();
    private readonly object _lock = new();

    /// <summary>Starts the service and registers a session token. Concurrent operators each add their own,
    /// so a second operator joining does not invalidate the first. Idempotent on the listener.</summary>
    public void Start(string token)
    {
        if (!string.IsNullOrEmpty(token)) lock (_lock) _tokens.Add(token);
        if (_listener is { IsListening: true }) return;
        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        try { _listener.Start(); }
        catch (Exception ex) { logger.LogWarning(ex, "File service could not bind 127.0.0.1:{Port}.", Port); return; }
        _ = AcceptLoopAsync(_cts.Token);
        logger.LogInformation("File service listening on 127.0.0.1:{Port}.", Port);
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
            lock (_lock) authorized = tok != null && _tokens.Contains(tok);
            if (!authorized) { ctx.Response.StatusCode = 403; ctx.Response.Close(); return; }

            var path = req.QueryString["path"] ?? "";
            switch (req.Url!.AbsolutePath.ToLowerInvariant())
            {
                case "/drives": await WriteJsonAsync(ctx, Drives()); break;
                case "/home": await WriteJsonAsync(ctx, ListDir(HomeDir())); break;
                case "/list": await WriteJsonAsync(ctx, ListDir(path)); break;
                case "/download": await DownloadAsync(ctx, path); break;
                case "/upload": await UploadAsync(ctx, path); break;
                case "/mkdir": Directory.CreateDirectory(path); Audit("mkdir", path); Ok(ctx); break;
                case "/delete": DeleteEntry(path); Audit("delete", path); Ok(ctx); break;
                case "/rename": RenameEntry(path, req.QueryString["to"] ?? ""); Audit("rename", $"{path} -> {req.QueryString["to"]}"); Ok(ctx); break;
                default: ctx.Response.StatusCode = 404; ctx.Response.Close(); break;
            }
        }
        catch (Exception ex)
        {
            try { ctx.Response.StatusCode = 500; var b = Encoding.UTF8.GetBytes(ex.Message); ctx.Response.OutputStream.Write(b); ctx.Response.Close(); } catch { /* client gone */ }
        }
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

    private async Task DownloadAsync(HttpListenerContext ctx, string path)
    {
        ctx.Response.ContentType = "application/octet-stream";
        await using var fs = File.OpenRead(path);
        ctx.Response.ContentLength64 = fs.Length;
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
        Audit("download", path);
    }

    private async Task UploadAsync(HttpListenerContext ctx, string path)
    {
        await using (var fs = File.Create(path))
            await ctx.Request.InputStream.CopyToAsync(fs);
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

    private void Audit(string op, string detail) => logger.LogInformation("file {Op}: {Detail}", op, detail);

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
        try { _listener?.Stop(); _listener?.Close(); } catch { /* best effort */ }
        await Task.CompletedTask;
    }
}
