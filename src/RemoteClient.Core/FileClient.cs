using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>Where a file session is reached: the broker-forwarded local port and the session's file token.</summary>
public sealed record FileSession(int LocalPort, string Token);

/// <summary>
/// HTTP client to a device's loopback file service, reached through the broker-forwarded local port and
/// gated by the per-session token. Used by the remote pane of the file manager.
/// </summary>
public sealed class FileClient : IDisposable
{
    private volatile HttpClient _http;
    // Clients replaced by Retarget are disposed only with this one: a request still running on one would
    // otherwise fail as cancelled, which reads as the operator pressing Cancel.
    private readonly List<HttpClient> _retired = [];

    public FileClient(int localPort, string token) => _http = Create(localPort, token);

    private static HttpClient Create(int localPort, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}/"), Timeout = TimeSpan.FromHours(2) };
        http.DefaultRequestHeaders.Add("X-File-Token", token);
        return http;
    }

    /// <summary>
    /// Points the client at a new session after the old one went away (the tunnel dropped, the device cleared
    /// its tokens): the next request goes to the new port with the new token.
    /// </summary>
    public void Retarget(FileSession session)
    {
        var old = _http;
        _http = Create(session.LocalPort, session.Token);
        lock (_retired) _retired.Add(old);
    }

    /// <summary>
    /// Whether a failure means the session is gone rather than the file being a problem: no connection, a
    /// connection cut mid-transfer, or the device no longer taking the token (it clears them when the tunnel
    /// closes). A new session fixes these; an error about the file itself (missing, locked, denied) it does not.
    /// </summary>
    public static bool IsConnectionLost(Exception ex) => ex switch
    {
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.BadGateway
                                  or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout,
        HttpIOException => true,
        IOException { InnerException: SocketException } => true,
        _ => false,
    };

    public Task<FsList?> ListAsync(string path, CancellationToken ct) => GetListAsync($"list?path={Uri.EscapeDataString(path)}", ct);
    public Task<FsList?> DrivesAsync(CancellationToken ct) => GetListAsync("drives", ct);
    public Task<FsList?> HomeAsync(CancellationToken ct) => GetListAsync("home", ct);

    private async Task<FsList?> GetListAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.FsList, ct);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync($"download?path={Uri.EscapeDataString(path)}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct);
    }

    public async Task UploadAsync(string path, Stream content, CancellationToken ct)
    {
        using var sc = new StreamContent(content);
        using var resp = await _http.PostAsync($"upload?path={Uri.EscapeDataString(path)}", sc, ct);
        resp.EnsureSuccessStatusCode();
    }

    private async Task PostAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(url, null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public Task MkdirAsync(string path, CancellationToken ct) => PostAsync($"mkdir?path={Uri.EscapeDataString(path)}", ct);
    public Task DeleteAsync(string path, CancellationToken ct) => PostAsync($"delete?path={Uri.EscapeDataString(path)}", ct);
    public Task RenameAsync(string from, string to, CancellationToken ct) => PostAsync($"rename?path={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", ct);

    public void Dispose()
    {
        _http.Dispose();
        lock (_retired) { foreach (var h in _retired) h.Dispose(); _retired.Clear(); }
    }
}
