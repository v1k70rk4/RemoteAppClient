using System.Net.Http;
using System.Net.Http.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// HTTP client to a device's loopback file service, reached through the broker-forwarded local port and
/// gated by the per-session token. Used by the remote pane of the file manager.
/// </summary>
public sealed class FileClient : IDisposable
{
    private readonly HttpClient _http;

    public FileClient(int localPort, string token)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}/"), Timeout = TimeSpan.FromHours(2) };
        _http.DefaultRequestHeaders.Add("X-File-Token", token);
    }

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

    public void Dispose() => _http.Dispose();
}
