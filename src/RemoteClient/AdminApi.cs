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

    /// <summary>Egy Pending gép jóváhagyása (Status → Approved).</summary>
    public Task ApproveDeviceAsync(string deviceId, CancellationToken ct = default) =>
        UpdateDeviceAsync(deviceId, new DeviceUpdate { Status = "Approved" }, ct);

    /// <summary>Bootstrap blob generálása (site-token + szerver-URL egy stringben). A blobot adja vissza.</summary>
    public async Task<string?> CreateBootstrapAsync(int maxUses, int? expiresInHours, CancellationToken ct = default)
    {
        var q = $"/admin/bootstrap?maxUses={maxUses}" + (expiresInHours is { } h ? $"&expiresInHours={h}" : "");
        using var resp = await _http.PostAsync(q, content: null, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("blob", out var b) ? b.GetString() : null;
    }

    /// <summary>A csatornák aktuális csomagjai (komponensenként).</summary>
    public async Task<List<ChannelPackageInfo>> GetChannelsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/channels", AgentJsonContext.Default.ListChannelPackageInfo, ct) ?? [];

    /// <summary>Egy csatorna aktuális csomagjának kiadása az ott lévő gépeknek. A szerver JSON-válaszát adja vissza.</summary>
    public async Task<string> RolloutAsync(string channel, string component, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/channels/{channel}/rollout?component={component}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Egy csatorna aktuális csomagjának előléptetése a cél-csatornába (ugyanaz a fájl).</summary>
    public async Task<string> PromoteAsync(string fromChannel, string component, string toChannel, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/channels/{fromChannel}/promote?component={component}&to={toChannel}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public void Dispose() => _http.Dispose();
}
