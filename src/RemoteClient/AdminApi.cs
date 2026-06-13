using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>Bejelentkezési hiba a szerver hibakódjával (invalid_credentials / totp_required / totp_invalid / …).</summary>
public sealed class AuthException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// A szerver admin API-ja az SSH-forwardolt localhost porton keresztül. A forwardot a HELYI agent
/// brókere adja (<paramref name="openForward"/>). A kapcsolat ÖNGYÓGYÍTÓ: ha a tunnel meghal (pl. a gép
/// alvása után a 127.0.0.1 port elutasít), a ConnectCallback automatikusan kér egy FRISS forwardot és
/// újrapróbál — a hívó hibaüzenet helyett (pár mp ssh-handshake késéssel) működő választ kap.
/// </summary>
public sealed class AdminApi : IDisposable
{
    private readonly Func<CancellationToken, Task<int>> _openForward;
    private readonly SemaphoreSlim _forwardGate = new(1, 1);
    private volatile int _port; // az aktuális helyi forward-port; 0 = még nincs / újra kell nyitni
    private readonly HttpClient _http;

    public AdminApi(Func<CancellationToken, Task<int>> openForward)
    {
        _openForward = openForward;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectAsync,
            // Alvás után a poolban maradt kapcsolat holt; a ConnectCallback csak ÚJ kapcsolatra fut.
            // Rövid idle-timeouttal a (mindig >15s) alvás után biztosan friss kapcsolat épül → újraforward.
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        };
        // 10 perc: a nagy exe-feltöltés / MSI-gyártás belefér (a sima lekérdezések így is gyorsak).
        // A BaseAddress hosztja lényegtelen — a tényleges célt a ConnectCallback dönti el (a friss port).
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1"), Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Kapcsolat felépítése a tunnel aktuális portjához; halott tunnelnél friss forward + újrapróba.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        int port = _port;
        if (port != 0)
        {
            try { return await DialAsync(port, ct); }
            catch (SocketException) { /* halott tunnel (alvás után) → lent friss forward */ }
            catch (IOException) { }
        }

        await _forwardGate.WaitAsync(ct);
        try
        {
            // Lehet, hogy közben egy másik hívás már újranyitotta — próbáljuk azt előbb.
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

    /// <summary>Friss forward dialja: a hideg ssh -L handshake pár másodpercig tarthat, ezért ~15s-ig próbálkozunk.</summary>
    private static async ValueTask<Stream> DialWithWarmupAsync(int port, CancellationToken ct)
    {
        for (int i = 0; ; i++)
        {
            try { return await DialAsync(port, ct); }
            catch (SocketException) when (i < 15) { await Task.Delay(1000, ct); }
            catch (IOException) when (i < 15) { await Task.Delay(1000, ct); }
        }
    }

    /// <summary>A session-token beállítása minden további híváshoz (Bearer).</summary>
    public void SetToken(string? token) =>
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrWhiteSpace(token) ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    /// <summary>Bejelentkezés. Sikertelennél AuthException-t dob a szerver hibakódjával.</summary>
    public async Task<LoginResponse> LoginAsync(string username, string password, string? totp,
        string? clientVersion = null, string? channel = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new LoginRequest { Username = username, Password = password, Totp = totp, ClientVersion = clientVersion, Channel = channel },
            AgentJsonContext.Default.LoginRequest);
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

    // === Windows Hello (passkey-stílus) ===
    /// <summary>Belépési challenge kérése (még nincs session). A nyers nonce-t adja vissza.</summary>
    public async Task<byte[]> HelloChallengeAsync(string username, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new HelloChallengeRequest { Username = username }, AgentJsonContext.Default.HelloChallengeRequest);
        using var resp = await _http.PostAsync("/auth/hello/challenge", content, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.HelloChallengeResponse, ct);
        return Convert.FromBase64String(r!.Challenge);
    }

    /// <summary>Belépés az aláírt challenge-dzsel. Sikertelennél AuthException.</summary>
    public async Task<LoginResponse> HelloLoginAsync(string username, Guid credentialId, string signatureBase64,
        string? clientVersion = null, string? channel = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(
            new HelloLoginRequest { Username = username, CredentialId = credentialId, Signature = signatureBase64, ClientVersion = clientVersion, Channel = channel },
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

    /// <summary>Hello-eszköz regisztrálása a bejelentkezett userhez (publikus kulcs + eszköznév). A credentialId-t adja vissza.</summary>
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

    /// <summary>Gyors health-ping a szerverre (a „/" végpont „RemoteServer up."-ot ad).</summary>
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

    /// <summary>Napló (audit) lekérdezés szűrőkkel. Üres szűrő = mind. action/actor/deviceId opcionális.</summary>
    public async Task<List<AuditEntryInfo>> GetAuditAsync(string? action = null, string? actor = null, string? deviceId = null, int limit = 200, CancellationToken ct = default)
    {
        var q = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrWhiteSpace(action)) q.Add($"action={Uri.EscapeDataString(action)}");
        if (!string.IsNullOrWhiteSpace(actor)) q.Add($"actor={Uri.EscapeDataString(actor)}");
        if (!string.IsNullOrWhiteSpace(deviceId)) q.Add($"deviceId={Uri.EscapeDataString(deviceId)}");
        return await _http.GetFromJsonAsync($"/admin/audit?{string.Join("&", q)}", AgentJsonContext.Default.ListAuditEntryInfo, ct) ?? [];
    }

    // --- Szerver-beállítások (branding + e-mail) ---
    public async Task<ServerSettingsInfo> GetSettingsAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/settings", AgentJsonContext.Default.ServerSettingsInfo, ct) ?? new ServerSettingsInfo();

    public async Task UpdateSettingsAsync(ServerSettingsInfo s, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(s, AgentJsonContext.Default.ServerSettingsInfo);
        using var resp = await _http.PutAsync("/admin/settings", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Teszt-e-mail az aktív providerrel. (ok, error) — error a szerver hibaüzenete.</summary>
    public async Task<(bool Ok, string? Error)> TestEmailAsync(string to, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new TestEmailRequest { To = to }, AgentJsonContext.Default.TestEmailRequest);
        using var resp = await _http.PostAsync("/admin/settings/test-email", content, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        string body = ""; try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }
        return (false, string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body);
    }

    /// <summary>Publikus branding (a tunnelen át, bejelentkezés előtt is). Null hiba esetén.</summary>
    public async Task<BrandingInfo?> GetBrandingAsync(CancellationToken ct = default)
    {
        try { return await _http.GetFromJsonAsync("/admin/branding", AgentJsonContext.Default.BrandingInfo, ct); }
        catch { return null; }
    }

    /// <summary>A hozzáférés-kérés kimenetele (nonce alapján). Üres = még nincs válasz (várj tovább).</summary>
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

    /// <summary>Egy Pending gép jóváhagyása (Status → Approved).</summary>
    public Task ApproveDeviceAsync(string deviceId, CancellationToken ct = default) =>
        UpdateDeviceAsync(deviceId, new DeviceUpdate { Status = "Approved" }, ct);

    /// <summary>Bootstrap blob generálása (site-token + szerver-URL egy stringben), opcionálisan csoportra + lejárattal. A blobot adja vissza.</summary>
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

    // === Bootstrap-tokenek (blob-ok) kezelése ===
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

    /// <summary>Blob/token módosítása (max telepítés és/vagy lejárat). A null mezők változatlanok. Hibakódot dob (pl. max_below_used).</summary>
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

    // === Csoport-kezelés ===
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

    /// <summary>MSI legyártása egy csoporthoz egy csatornából (opcionálisan a konzol-klienssel + Start menü parancsikonnal). A (fájlnév, letöltési-url) párt adja vissza.</summary>
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

    /// <summary>Legyártott MSI letöltése helyi fájlba.</summary>
    public async Task DownloadMsiAsync(string fileName, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/admin/msi/{fileName}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    /// <summary>Egy release-csomag (exe) letöltése a /api/updates-ről (a kliens önfrissítéséhez).</summary>
    public async Task DownloadUpdateAsync(string fileName, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"/api/updates/{fileName}", HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(destPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    // === User-kezelés (admin) ===
    public async Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) =>
        await _http.GetFromJsonAsync("/admin/users", AgentJsonContext.Default.ListUserInfo, ct) ?? [];

    public async Task<CreateUserResponse> CreateUserAsync(string username, string? email, string role, string? name = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new CreateUserRequest { Username = username, Email = email, Role = role, Name = name }, AgentJsonContext.Default.CreateUserRequest);
        using var resp = await _http.PostAsync("/admin/users", content, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.CreateUserResponse, ct))!;
    }

    public async Task UpdateUserAsync(Guid id, string? role, bool? isActive, string? name = null, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(new UserUpdate { Role = role, IsActive = isActive, Name = name }, AgentJsonContext.Default.UserUpdate);
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

    /// <summary>Egy user Windows Hello eszközei (admin).</summary>
    public async Task<List<HelloCredentialInfo>> GetUserHelloAsync(Guid userId, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync($"/admin/users/{userId}/hello", AgentJsonContext.Default.ListHelloCredentialInfo, ct) ?? [];

    public async Task RevokeUserHelloAsync(Guid userId, Guid credId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/admin/users/{userId}/hello/{credId}/revoke", content: null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        try { _http.Dispose(); } catch { /* best effort */ }
        try { _forwardGate.Dispose(); } catch { /* best effort */ }
    }
}
