using System.Net.Http.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>A szerver admin API-ja, az SSH-forwardolt localhost porton keresztül.</summary>
public sealed class AdminApi(string baseUrl) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/devices", AgentJsonContext.Default.ListDeviceInfo, ct) ?? [];

    public async Task<OpenTunnelResult?> OpenTunnelAsync(string deviceId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/open-tunnel", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.OpenTunnelResult, ct);
    }

    public async Task<List<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/groups", AgentJsonContext.Default.ListGroupInfo, ct) ?? [];

    public async Task UpdateDeviceAsync(string deviceId, DeviceUpdate upd, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(upd, AgentJsonContext.Default.DeviceUpdate);
        using var resp = await _http.PutAsync($"/admin/devices/{deviceId}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
