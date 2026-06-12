using System.Net.Http.Headers;
using System.Net.Http.Json;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>Bejelentkezési hiba a szerver hibakódjával (invalid_credentials / totp_required / totp_invalid / …).</summary>
public sealed class AuthException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>A szerver admin API-ja, az SSH-forwardolt localhost porton keresztül.</summary>
public sealed class AdminApi(string baseUrl) : IDisposable
{
    // 10 perc: a nagy exe-feltöltés / MSI-gyártás belefér (a sima lekérdezések így is gyorsak).
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>A session-token beállítása minden további híváshoz (Bearer).</summary>
    public void SetToken(string? token) =>
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(token) ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    /// <summary>Bejelentkezés. Sikertelennél AuthException-t dob a szerver hibakódjával.</summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, string? totp, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new LoginRequest { Username = username, Password = password, Totp = totp }, AgentJsonContext.Default.LoginRequest);
        using var resp = await _http.PostAsync("/auth/login", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string code = $"http_{(int)resp.StatusCode}";
            try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); if (!string.IsNullOrEmpty(e?.Error)) code = e.Error; }
            catch { /* nem JSON */ }
            throw new AuthException(code);
        }
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.LoginResponse, ct))!;
    }

    public async Task ChangePasswordAsync(string newPassword, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new ChangePasswordRequest { NewPassword = newPassword }, AgentJsonContext.Default.ChangePasswordRequest);
        using var resp = await _http.PostAsync("/auth/change-password", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ConfirmTotpAsync(string code, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new TotpConfirmRequest { Code = code }, AgentJsonContext.Default.TotpConfirmRequest);
        using var resp = await _http.PostAsync("/auth/totp/confirm", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try { using var resp = await _http.PostAsync("/auth/logout", content: null, ct); } catch { /* best effort */ }
    }

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

    /// <summary>Egy exe feltöltése egy csatornára (component: agent/updater). A szerver JSON-válaszát adja vissza.</summary>
    public async Task<string> UploadPackageAsync(string channel, string component, string version, string filePath, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(filePath);
        using var content = new StreamContent(fs);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PostAsync(
            $"/admin/packages?channel={channel}&component={component}&version={Uri.EscapeDataString(version)}", content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>MSI legyártása egy csoporthoz egy csatornából. A (fájlnév, letöltési-url) párt adja vissza.</summary>
    public async Task<(string fileName, string url)> BuildMsiAsync(Guid? groupId, string channel, CancellationToken ct = default)
    {
        var q = $"/admin/msi?channel={channel}" + (groupId is { } g ? $"&group={g}" : "");
        using var resp = await _http.PostAsync(q, content: null, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return (root.GetProperty("fileName").GetString() ?? "", root.GetProperty("url").GetString() ?? "");
    }

    /// <summary>Legyártott MSI letöltése helyi fájlba.</summary>
    public async Task DownloadMsiAsync(string fileName, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/admin/msi/{fileName}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    // === User-kezelés (admin) ===
    public async Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/users", AgentJsonContext.Default.ListUserInfo, ct) ?? [];

    public async Task<CreateUserResponse> CreateUserAsync(string username, string? email, string role, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new CreateUserRequest { Username = username, Email = email, Role = role }, AgentJsonContext.Default.CreateUserRequest);
        using var resp = await _http.PostAsync("/admin/users", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.CreateUserResponse, ct))!;
    }

    public async Task UpdateUserAsync(Guid id, string? role, bool? isActive, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new UserUpdate { Role = role, IsActive = isActive }, AgentJsonContext.Default.UserUpdate);
        using var resp = await _http.PutAsync($"/admin/users/{id}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<CreateUserResponse> ResetPasswordAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{id}/reset-password", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.CreateUserResponse, ct))!;
    }

    public async Task RevokeSessionsAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{id}/revoke-sessions", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<GrantInfo>> GetGrantsAsync(Guid id, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync($"/admin/users/{id}/grants", AgentJsonContext.Default.ListGrantInfo, ct) ?? [];

    public async Task AddGrantAsync(Guid id, Guid? groupId, string? deviceId, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new GrantRequest { GroupId = groupId, DeviceId = deviceId }, AgentJsonContext.Default.GrantRequest);
        using var resp = await _http.PostAsync($"/admin/users/{id}/grants", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RemoveGrantAsync(Guid userId, Guid grantId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/admin/users/{userId}/grants/{grantId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
