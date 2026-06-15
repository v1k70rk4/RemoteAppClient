using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>Sign-in error carrying the server error code (invalid_credentials / totp_required / totp_invalid / ...).</summary>
public sealed class AuthException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Server admin API through an SSH-forwarded localhost port. The forward is provided by
/// the local agent broker (<paramref name="openForward"/>). The connection self-heals:
/// when the tunnel dies, for example after sleep and a refused 127.0.0.1 port, ConnectCallback
/// requests a fresh forward and retries, giving the caller a working response after the SSH delay.
/// </summary>
public sealed class AdminApi : IDisposable
{
    private readonly Func<CancellationToken, Task<int>> _openForward;
    private readonly SemaphoreSlim _forwardGate = new(1, 1);
    private volatile int _port; // current local forward port; 0 = not opened yet / reopen required
    private readonly HttpClient _http;

    /// <summary>Local agent device ID from the status pipe, sent with login/reset for the device-level fail counter.</summary>
    public string? DeviceId { get; set; }

    public AdminApi(Func<CancellationToken, Task<int>> openForward)
    {
        _openForward = openForward;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            // Connections left in the pool after sleep are dead; ConnectCallback runs only for new ones.
            // Short idle timeout ensures sleep creates a fresh connection and therefore a fresh forward.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        };
        // 10 minutes: large exe upload / MSI generation fits, while normal queries remain quick.
        // BaseAddress host is irrelevant; ConnectCallback decides the actual fresh port target.
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1"), Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Connects to the current tunnel port; dead tunnels trigger fresh forward plus retry.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        int port = _port;
        if (port != 0)
        {
            try { return await DialAsync(port, ct); }
            catch (SocketException) { /* dead tunnel after sleep; fresh forward below */ }
            catch (IOException) { }
        }

        await _forwardGate.WaitAsync(ct);
        try
        {
            // Another call may have reopened it already; try that first.
            if (_port != 0 && _port != port)
            {
                try { return await DialAsync(_port, ct); }
                catch (SocketException) { }
                catch (IOException) { }
            }
            int fresh = await _openForward(ct);
            _port = fresh;
            return await DialWithWarmupAsync(fresh, ct);
        }
        finally { _forwardGate.Release(); }
    }

    private static async ValueTask<Stream> DialAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Dials a fresh forward. Cold ssh -L handshake can take seconds, so retry for about 15s.</summary>
    private static async ValueTask<Stream> DialWithWarmupAsync(int port, CancellationToken ct)
    {
        for (int i = 0; ; i++)
        {
            try { return await DialAsync(port, ct); }
            catch (SocketException) when (i < 15) { await Task.Delay(1000, ct); }
            catch (IOException) when (i < 15) { await Task.Delay(1000, ct); }
        }
    }

    /// <summary>Sets the bearer session token for subsequent calls.</summary>
    public void SetToken(string? token) =>
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(token) ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    /// <summary>Signs in. On failure, throws AuthException with the server error code.</summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, string? totp,
        string? clientVersion = null, string? channel = null, string? trustToken = null, bool rememberDevice = false, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new LoginRequest { Username = username, Password = password, Totp = totp, ClientVersion = clientVersion, Channel = channel, DeviceId = DeviceId, TrustToken = trustToken, RememberDevice = rememberDevice },
            AgentJsonContext.Default.LoginRequest);
        using var resp = await _http.PostAsync("/auth/login", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string code = $"http_{(int)resp.StatusCode}";
            try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); if (!string.IsNullOrEmpty(e?.Error)) code = e.Error; }
            catch { /* non-JSON */ }
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

    /// <summary>Saves the signed-in operator's TightVNC viewer prefs: scale ("auto" or "1".."400") and color ("full"/"256"). Roams with the account.</summary>
    public async Task UpdateViewerPrefsAsync(string scale, string color, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new ViewerPrefsRequest { Scale = scale, Color = color }, AgentJsonContext.Default.ViewerPrefsRequest);
        using var resp = await _http.PutAsync("/admin/me/viewer-prefs", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    // === Windows Hello (passkey-style) ===
    /// <summary>Requests a sign-in challenge before a session exists. Returns the raw nonce.</summary>
    public async Task<byte[]> HelloChallengeAsync(string username, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new HelloChallengeRequest { Username = username }, AgentJsonContext.Default.HelloChallengeRequest);
        using var resp = await _http.PostAsync("/auth/hello/challenge", content, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.HelloChallengeResponse, ct);
        return Convert.FromBase64String(r!.Challenge);
    }

    /// <summary>Signs in with the signed challenge. Throws AuthException on failure.</summary>
    public async Task<LoginResponse> HelloLoginAsync(string username, Guid credentialId, string signatureBase64,
        string? clientVersion = null, string? channel = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new HelloLoginRequest { Username = username, CredentialId = credentialId, Signature = signatureBase64, DeviceId = DeviceId, ClientVersion = clientVersion, Channel = channel },
            AgentJsonContext.Default.HelloLoginRequest);
        using var resp = await _http.PostAsync("/auth/hello/login", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string code = $"http_{(int)resp.StatusCode}";
            try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); if (!string.IsNullOrEmpty(e?.Error)) code = e.Error; } catch { }
            throw new AuthException(code);
        }
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.LoginResponse, ct))!;
    }

    /// <summary>Registers a Hello device for the signed-in user (public key + device name). Returns credentialId.</summary>
    public async Task<Guid> RegisterHelloAsync(string publicKeyBase64, string deviceName, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new HelloRegisterRequest { PublicKey = publicKeyBase64, DeviceName = deviceName }, AgentJsonContext.Default.HelloRegisterRequest);
        using var resp = await _http.PostAsync("/auth/hello/register", content, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.HelloRegisterResponse, ct);
        return r!.CredentialId;
    }

    public async Task<List<HelloCredentialInfo>> GetHelloCredentialsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/auth/hello/credentials", AgentJsonContext.Default.ListHelloCredentialInfo, ct) ?? [];

    public async Task RevokeHelloAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/auth/hello/credentials/{id}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Quick server health ping; the "/" endpoint returns "RemoteServer up.".</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { using var r = await _http.GetAsync("/", ct); return r.IsSuccessStatusCode; }
        catch { return false; }
    }

    public async Task<List<DeviceInfo>> GetDevicesAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/devices", AgentJsonContext.Default.ListDeviceInfo, ct) ?? [];

    public async Task<OpenTunnelResult?> OpenTunnelAsync(string deviceId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/open-tunnel", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.OpenTunnelResult, ct);
    }

    /// <summary>Messages tab: asks the device user "is it free now?". Returns the nonce to poll the outcome.</summary>
    public async Task<string?> AskAvailabilityAsync(string deviceId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/ask-availability", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.OpenTunnelResult, ct))?.Nonce;
    }

    /// <summary>Messages tab: sends a plain message to the device user. Returns the nonce to poll delivery.</summary>
    public async Task<string?> SendMessageAsync(string deviceId, string text, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/send-message?text={Uri.EscapeDataString(text)}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.OpenTunnelResult, ct))?.Nonce;
    }

    /// <summary>Commands tab: runs a fixed power action (restart/force-restart/cancel/logout). Returns the nonce to poll.</summary>
    public async Task<string?> PowerAsync(string deviceId, string action, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/power?action={Uri.EscapeDataString(action)}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.OpenTunnelResult, ct))?.Nonce;
    }

    /// <summary>Fetches audit log with filters. Empty filter = all. action/actor/deviceId are optional.</summary>
    public async Task<List<AuditEntryInfo>> GetAuditAsync(string? action = null, string? actor = null, string? deviceId = null, int limit = 200, CancellationToken ct = default)
    {
        var q = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(action)) q.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrWhiteSpace(actor)) q.Add($"actor={Uri.EscapeDataString(actor)}");
        if (!string.IsNullOrWhiteSpace(deviceId)) q.Add($"deviceId={Uri.EscapeDataString(deviceId)}");
        return await _http.GetFromJsonAsync($"/admin/audit?{string.Join("&", q)}", AgentJsonContext.Default.ListAuditEntryInfo, ct) ?? [];
    }

    // --- Server settings (branding + email) ---
    public async Task<ServerSettingsInfo> GetSettingsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/settings", AgentJsonContext.Default.ServerSettingsInfo, ct) ?? new ServerSettingsInfo();

    public async Task UpdateSettingsAsync(ServerSettingsInfo s, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(s, AgentJsonContext.Default.ServerSettingsInfo);
        using var resp = await _http.PutAsync("/admin/settings", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Sends test email with the active provider. (ok, error), where error is the server message.</summary>
    public async Task<(bool Ok, string? Error)> TestEmailAsync(string to, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new TestEmailRequest { To = to }, AgentJsonContext.Default.TestEmailRequest);
        using var resp = await _http.PostAsync("/admin/settings/test-email", content, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        string body = ""; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
        return (false, string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body);
    }

    /// <summary>Public branding through the tunnel, available before sign-in. Null on error.</summary>
    public async Task<BrandingInfo?> GetBrandingAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync("/admin/branding", AgentJsonContext.Default.BrandingInfo, ct); }
        catch { return null; }
    }

    /// <summary>Access request outcome by nonce. Empty means no answer yet; keep waiting.</summary>
    public async Task<string> GetAccessResultAsync(string nonce, CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync($"/admin/devices/access-result/{nonce}", AgentJsonContext.Default.AccessResultInfo, ct);
        return r?.Outcome ?? "";
    }

    public async Task<List<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/groups", AgentJsonContext.Default.ListGroupInfo, ct) ?? [];

    public async Task UpdateDeviceAsync(string deviceId, DeviceUpdate upd, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(upd, AgentJsonContext.Default.DeviceUpdate);
        using var resp = await _http.PutAsync($"/admin/devices/{deviceId}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Approves a Pending device (Status -> Approved).</summary>
    public Task ApproveDeviceAsync(string deviceId, CancellationToken ct = default) =>
        UpdateDeviceAsync(deviceId, new DeviceUpdate { Status = "Approved" }, ct);

    /// <summary>Unlocks device login lockout by resetting the counter.</summary>
    public async Task UnlockDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/devices/{deviceId}/unlock", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Deletes a device and its dependent rows (telemetry, commands, sessions).</summary>
    public async Task DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/admin/devices/{deviceId}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Generates a bootstrap blob (site token + server URL in one string), optionally scoped to group and expiry. Returns the blob.</summary>
    public async Task<string?> CreateBootstrapAsync(int maxUses, int? expiresInHours, Guid? groupId = null, CancellationToken ct = default)
    {
        var q = $"/admin/bootstrap?maxUses={maxUses}"
            + (expiresInHours is { } h ? $"&expiresInHours={h}" : "")
            + (groupId is { } g ? $"&groupId={g}" : "");
        using var resp = await _http.PostAsync(q, content: null, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("blob", out var b) ? b.GetString() : null;
    }

    // === Bootstrap tokens (blobs) ===
    public async Task<List<BootstrapTokenInfo>> GetTokensAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/tokens-list", AgentJsonContext.Default.ListBootstrapTokenInfo, ct) ?? [];

    public async Task RevokeTokenAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/tokens-list/{id}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteTokenAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/admin/tokens-list/{id}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Edits a blob/token (max installs and/or expiry). Null fields stay unchanged. Throws error codes such as max_below_used.</summary>
    public async Task EditTokenAsync(Guid id, int? maxUses, int? expiresInHours, bool clearExpiry, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new EditTokenRequest { MaxUses = maxUses, ExpiresInHours = expiresInHours, ClearExpiry = clearExpiry },
            AgentJsonContext.Default.EditTokenRequest);
        using var resp = await _http.PutAsync($"/admin/tokens-list/{id}", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string code = $"http_{(int)resp.StatusCode}";
            try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); if (!string.IsNullOrEmpty(e?.Error)) code = e.Error; } catch { }
            throw new InvalidOperationException(code);
        }
    }

    // === Group management ===
    public async Task<GroupInfo> CreateGroupAsync(GroupInfo g, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(g, AgentJsonContext.Default.GroupInfo);
        using var resp = await _http.PostAsync("/admin/groups", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.GroupInfo, ct))!;
    }

    public async Task UpdateGroupAsync(Guid id, GroupInfo g, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(g, AgentJsonContext.Default.GroupInfo);
        using var resp = await _http.PutAsync($"/admin/groups/{id}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/admin/groups/{id}", ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Current channel packages per component.</summary>
    public async Task<List<ChannelPackageInfo>> GetChannelsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/channels", AgentJsonContext.Default.ListChannelPackageInfo, ct) ?? [];

    /// <summary>Rolls out the current package on a channel to devices there. Returns the server JSON response.</summary>
    public async Task<string> RolloutAsync(string channel, string component, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/channels/{channel}/rollout?component={component}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Promotes a channel's current package to the target channel using the same file.</summary>
    public async Task<string> PromoteAsync(string fromChannel, string component, string toChannel, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/channels/{fromChannel}/promote?component={component}&to={toChannel}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Uploads an exe to a channel (component: agent/updater). Returns the server JSON response.</summary>
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

    /// <summary>Builds an MSI for a group from a channel, optionally including the console client and Start menu shortcut. Returns file name and download URL.</summary>
    public async Task<(string fileName, string url)> BuildMsiAsync(Guid? groupId, string channel, bool includeClient = true, bool shortcut = true, CancellationToken ct = default)
    {
        var q = $"/admin/msi?channel={channel}"
            + (groupId is { } g ? $"&group={g}" : "")
            + $"&client={includeClient.ToString().ToLowerInvariant()}&shortcut={shortcut.ToString().ToLowerInvariant()}";
        using var resp = await _http.PostAsync(q, content: null, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return (root.GetProperty("fileName").GetString() ?? "", root.GetProperty("url").GetString() ?? "");
    }

    /// <summary>Downloads a generated MSI to a local file.</summary>
    public async Task DownloadMsiAsync(string fileName, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/admin/msi/{fileName}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    /// <summary>Downloads a release package (exe) from /api/updates for client self-update.</summary>
    public async Task DownloadUpdateAsync(string fileName, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/updates/{fileName}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    // === User management (admin) ===
    public async Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/users", AgentJsonContext.Default.ListUserInfo, ct) ?? [];

    public async Task<CreateUserResponse> CreateUserAsync(string username, string? email, string role, string? name = null, bool emailCode = false, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new CreateUserRequest { Username = username, Email = email, Role = role, Name = name, EmailCode = emailCode }, AgentJsonContext.Default.CreateUserRequest);
        using var resp = await _http.PostAsync("/admin/users", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.CreateUserResponse, ct))!;
    }

    /// <summary>Requests a password recovery code before login. Response is always OK for anti-enumeration.</summary>
    public async Task RequestPasswordCodeAsync(string username, string email, CancellationToken ct = default)
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        using var content = JsonContent.Create(new PasswordCodeRequest { Username = username, Email = email, DeviceId = DeviceId, Language = lang }, AgentJsonContext.Default.PasswordCodeRequest);
        using var resp = await _http.PostAsync("/auth/password/request-code", content, ct);
        // Intentionally do not throw: anti-enumeration hides whether a matching account exists.
    }

    /// <summary>Sets a new password with the received code. (ok, error).</summary>
    public async Task<(bool Ok, string? Error)> ResetPasswordWithCodeAsync(string username, string code, string newPassword, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new PasswordResetRequest { Username = username, Code = code, NewPassword = newPassword, DeviceId = DeviceId }, AgentJsonContext.Default.PasswordResetRequest);
        using var resp = await _http.PostAsync("/auth/password/reset", content, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        string code2 = ""; try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); code2 = e?.Error ?? ""; } catch { }
        return (false, code2);
    }

    public async Task UpdateUserAsync(Guid id, string? role, bool? isActive, string? name = null, string? email = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new UserUpdate { Role = role, IsActive = isActive, Name = name, Email = email }, AgentJsonContext.Default.UserUpdate);
        using var resp = await _http.PutAsync($"/admin/users/{id}", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Clears TOTP/authenticator alone, without password reset.</summary>
    public async Task ClearTotpAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{id}/clear-totp", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<CreateUserResponse> ResetPasswordAsync(Guid id, bool emailCode = false, bool clearTotp = false, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{id}/reset-password?emailCode={(emailCode ? "true" : "false")}&clearTotp={(clearTotp ? "true" : "false")}", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.CreateUserResponse, ct))!;
    }

    public async Task RevokeSessionsAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{id}/revoke-sessions", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/admin/users/{id}", ct);
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

    /// <summary>Windows Hello devices for a user (admin).</summary>
    public async Task<List<HelloCredentialInfo>> GetUserHelloAsync(Guid userId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync($"/admin/users/{userId}/hello", AgentJsonContext.Default.ListHelloCredentialInfo, ct) ?? [];

    public async Task RevokeUserHelloAsync(Guid userId, Guid credId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{userId}/hello/{credId}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Trusted ("remember this device") machines for a user (admin).</summary>
    public async Task<List<TrustedDeviceInfo>> GetUserTrustsAsync(Guid userId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync($"/admin/users/{userId}/trusts", AgentJsonContext.Default.ListTrustedDeviceInfo, ct) ?? [];

    public async Task RevokeUserTrustAsync(Guid userId, Guid trustId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{userId}/trusts/{trustId}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* best effort */ }
        try { _forwardGate.Dispose(); } catch { /* best effort */ }
    }
}
