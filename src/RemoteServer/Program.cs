using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteAgent.Admin;
using RemoteAgent.Commands;
using RemoteAgent.Enrollment;
using RemoteAgent.Telemetry;
using RemoteServer.Configuration;
using RemoteServer.Data;
using RemoteServer.Data.Entities;
using RemoteServer.Hub;
using RemoteServer.Security;
using RemoteServer.Services;
using RemoteServer.Signing;
using RemoteServer.Telemetry;
using L = RemoteServer.Localization.Strings;

RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();

var builder = WebApplication.CreateBuilder(args);

// Az update-csomag (agent exe) ~70-100 MB — a Kestrel alap ~28 MB limitjét megemeljük.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

// MariaDB (Galera) — EF Core 9 + Pomelo. A retry a Galera tranziens
// certifikációs/deadlock hibáit nyeli el. A connection string env/secretből jön.
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseMySql(
        builder.Configuration.GetConnectionString("MariaDb") ?? "",
        new MariaDbServerVersion(new Version(10, 11, 14)),
        my => my.EnableRetryOnFailure()));

builder.Services.AddSingleton<CommandSigner>();
builder.Services.AddSingleton<CertificateAuthority>();
builder.Services.AddSingleton<SshCertificateAuthority>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddScoped<ITelemetrySink, DbTelemetrySink>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddSingleton<MsiBuilder>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<HelloChallengeStore>();
builder.Services.AddSingleton<AccessResultStore>();
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.AddHostedService<SecretExpiryWatcher>();

var app = builder.Build();
app.UseWebSockets();

// === Session-auth az /admin-ra: érvényes Bearer-token kell (a transportot a gép SSH-tunnelje adja).
// A /auth/* végpontok publikusak (a tunnelen át), és maguk validálnak. Amíg a user setupja
// (jelszócsere / TOTP-enroll) nincs kész, a konzol-végpontok 403-at adnak. ===
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/admin")) { await next(); return; }

    // A branding (tulajdonos + support) publikus a tunnelen át — bejelentkezés ELŐTT is kell.
    // (Az nginx az /admin/-t csak localhostról, a tunnelen át engedi, így kintről nem elérhető.)
    if (ctx.Request.Path.StartsWithSegments("/admin/branding")) { await next(); return; }

    var auth = ctx.RequestServices.GetRequiredService<AuthService>();
    var token = BearerToken(ctx);
    var v = await auth.ValidateAsync(token, ctx.RequestAborted);
    if (v is null)
    {
        ctx.Response.StatusCode = 401;
        await ctx.Response.WriteAsJsonAsync(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError);
        return;
    }
    if (v.Value.User.MustChangePassword || !v.Value.User.TotpConfirmed)
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new AuthError { Error = "setup_incomplete" }, AgentJsonContext.Default.AuthError);
        return;
    }

    // Szerep-gating: az operator CSAK a géplistát/csoportlistát látja és csak open-tunnelt indíthat
    // (a szűrést/grant-ellenőrzést a végpontok végzik); MINDEN más /admin végpont admin-only.
    var user = v.Value.User;
    if (!AuthService.IsAdmin(user))
    {
        var m = ctx.Request.Method;
        var p = ctx.Request.Path.Value ?? "";
        bool operatorAllowed =
            (m == "GET" && p == "/admin/devices")
            || (m == "POST" && p.StartsWith("/admin/devices/", StringComparison.Ordinal) && p.EndsWith("/open-tunnel", StringComparison.Ordinal));
        if (!operatorAllowed)
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new AuthError { Error = "forbidden" }, AgentJsonContext.Default.AuthError);
            return;
        }
    }

    ctx.Items["user"] = user;
    await next();
});

app.MapGet("/", () => "RemoteServer up.");

// === Bejelentkezés / 2FA (a gép SSH-tunneljén át érhető el; maguk validálnak) ===
app.MapPost("/auth/login", async (HttpContext ctx, AppDbContext db, AuthService auth, SecretProtector protector, IEmailSender email, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    LoginRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.LoginRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest();

    // Min-verzió kapu: elavult kliens nem kap sessiont, hanem kötelező frissítés-infót.
    if (await ClientUpdateGateAsync(req.ClientVersion, req.Channel, opt.Value.MinClientVersion, db, ct) is { } gate)
        return Results.Json(gate, AgentJsonContext.Default.LoginResponse);

    // Gép-szintű belépés-zárolás: ha a gép zárolva, semmilyen belépés nem megy (csak admin oldja fel).
    var device = await FindDeviceAsync(db, req.DeviceId, ct);
    if (device?.LoginLockedAt is not null)
        return Results.Json(new AuthError { Error = "device_locked" }, AgentJsonContext.Default.AuthError, statusCode: 403);

    var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
        .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
    {
        await RegisterLoginFailAsync(db, email, ctx, device, req.Username, "login-failed",
            user is null ? L.Program_001 : L.Program_002, ct);
        return Results.Json(new AuthError { Error = "invalid_credentials" }, AgentJsonContext.Default.AuthError, statusCode: 401);
    }

    // Ha már be van állítva a TOTP, kötelező a kód.
    if (user.TotpConfirmed)
    {
        var secret = protector.TryUnprotect(user.TotpSecret);
        if (secret is null || !TotpService.Verify(secret, req.Totp ?? ""))
        {
            // A hibás TOTP is sikertelen próba — de a „totp_required" (még nem írt be kódot) nem.
            if (!string.IsNullOrWhiteSpace(req.Totp))
                await RegisterLoginFailAsync(db, email, ctx, device, req.Username, "login-failed", L.Program_003, ct);
            return Results.Json(new AuthError { Error = string.IsNullOrWhiteSpace(req.Totp) ? "totp_required" : "totp_invalid" },
                AgentJsonContext.Default.AuthError, statusCode: 401);
        }
    }

    await ResetLoginFailAsync(db, device, ct); // sikeres belépés → számláló nullázása
    var token = await auth.CreateSessionAsync(user, ct);
    user.LastLoginAt = DateTimeOffset.UtcNow;

    var resp = new LoginResponse
    {
        Token = token,
        Role = AuthService.RoleOf(user),
        MustChangePassword = user.MustChangePassword,
        TotpEnrollRequired = !user.TotpConfirmed,
    };

    // Első belépés / nincs még TOTP: generálunk titkot az enrollhoz (titkosítva tároljuk, még nem confirmed).
    if (!user.TotpConfirmed)
    {
        var secret = TotpService.GenerateSecret();
        user.TotpSecret = protector.Protect(secret);
        resp.TotpSecret = secret;
        resp.TotpUri = TotpService.BuildUri(secret, user.Username, "RemoteAppClient");
    }

    await db.SaveChangesAsync(ct);
    return Results.Json(resp, AgentJsonContext.Default.LoginResponse);
});

// === Windows Hello (passkey-stílus) ===
// Belépés 1/2: challenge. Mindig adunk (nem áruljuk el, létezik-e a user / van-e Hello).
app.MapPost("/auth/hello/challenge", async (HttpContext ctx, HelloChallengeStore challenges, CancellationToken ct) =>
{
    HelloChallengeRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.HelloChallengeRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest();
    var nonce = challenges.Issue(req.Username.Trim());
    return Results.Json(new HelloChallengeResponse { Challenge = Convert.ToBase64String(nonce) }, AgentJsonContext.Default.HelloChallengeResponse);
});

// Belépés 2/2: az aláírt challenge ellenőrzése a tárolt publikus kulccsal → session.
app.MapPost("/auth/hello/login", async (HttpContext ctx, AppDbContext db, AuthService auth, HelloChallengeStore challenges, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    HelloLoginRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.HelloLoginRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Signature))
        return Results.Json(new AuthError { Error = "invalid_request" }, AgentJsonContext.Default.AuthError, statusCode: 400);

    // Min-verzió kapu (ld. /auth/login): elavult kliens kötelező frissítést kap.
    if (await ClientUpdateGateAsync(req.ClientVersion, req.Channel, opt.Value.MinClientVersion, db, ct) is { } gate)
        return Results.Json(gate, AgentJsonContext.Default.LoginResponse);

    var nonce = challenges.Consume(req.Username.Trim());
    if (nonce is null) return Results.Json(new AuthError { Error = "challenge_expired" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
        .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);
    if (user is null) return Results.Json(new AuthError { Error = "invalid_credentials" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    var cred = await db.HelloCredentials.FirstOrDefaultAsync(c => c.Id == req.CredentialId && c.UserId == user.Id && c.RevokedAt == null, ct);
    if (cred is null) return Results.Json(new AuthError { Error = "hello_unknown" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    bool ok;
    try
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(cred.PublicKey), out _);
        ok = rsa.VerifyData(nonce, Convert.FromBase64String(req.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
    catch { ok = false; }
    if (!ok) return Results.Json(new AuthError { Error = "hello_invalid" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    cred.LastUsedAt = DateTimeOffset.UtcNow;
    var token = await auth.CreateSessionAsync(user, ct);
    user.LastLoginAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Json(new LoginResponse
    {
        Token = token,
        Role = AuthService.RoleOf(user),
        MustChangePassword = user.MustChangePassword,
        TotpEnrollRequired = !user.TotpConfirmed,
    }, AgentJsonContext.Default.LoginResponse);
});

// Hello-eszköz regisztrálása (bejelentkezve; bármely user a sajátját — ezért itt validálunk, nem az /admin gate-en).
app.MapPost("/auth/hello/register", async (HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    HelloRegisterRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.HelloRegisterRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.PublicKey)) return Results.BadRequest();
    try { using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(req.PublicKey), out _); }
    catch { return Results.BadRequest(new { error = "bad_public_key" }); }

    var cred = new HelloCredential
    {
        UserId = v.Value.User.Id,
        PublicKey = req.PublicKey,
        DeviceName = string.IsNullOrWhiteSpace(req.DeviceName) ? L.Program_004 : req.DeviceName.Trim(),
    };
    db.HelloCredentials.Add(cred);
    await db.SaveChangesAsync(ct);
    return Results.Json(new HelloRegisterResponse { CredentialId = cred.Id }, AgentJsonContext.Default.HelloRegisterResponse);
});

app.MapGet("/auth/hello/credentials", async (HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);
    var list = await db.HelloCredentials.Where(c => c.UserId == v.Value.User.Id && c.RevokedAt == null)
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new HelloCredentialInfo { Id = c.Id, DeviceName = c.DeviceName, CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt })
        .ToListAsync(ct);
    return Results.Json(list, AgentJsonContext.Default.ListHelloCredentialInfo);
});

app.MapPost("/auth/hello/credentials/{id:guid}/revoke", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);
    var cred = await db.HelloCredentials.FirstOrDefaultAsync(c => c.Id == id && c.UserId == v.Value.User.Id, ct);
    if (cred is null) return Results.NotFound();
    cred.RevokedAt ??= DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// A setup-lépések (mid-setup user is hívhatja, ezért itt validálunk, nem az /admin gate-en).
app.MapPost("/auth/change-password", async (HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    ChangePasswordRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.ChangePasswordRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || (req.NewPassword?.Length ?? 0) < 10)
        return Results.Json(new AuthError { Error = "weak_password" }, AgentJsonContext.Default.AuthError, statusCode: 400);

    var user = await db.Users.FirstAsync(u => u.Id == v.Value.User.Id, ct);
    user.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
    user.MustChangePassword = false;
    user.PasswordChangedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// === Jelszó-emlékeztető (publikus, a tunnelen át, bejelentkezés ELŐTT) ===
// Kód kérése: a válasz MINDIG OK (anti-enumeration). Csak akkor küld kódot, ha a
// felhasználónév + e-mail egy aktív userre illik.
app.MapPost("/auth/password/request-code", async (HttpContext ctx, AppDbContext db, IEmailSender email, CancellationToken ct) =>
{
    PasswordCodeRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.PasswordCodeRequest, ct); }
    catch (JsonException) { return Results.NoContent(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Email))
        return Results.NoContent();

    var uname = req.Username.Trim();
    var mail = req.Email.Trim();
    var device = await FindDeviceAsync(db, req.DeviceId, ct);
    if (device?.LoginLockedAt is not null) return Results.NoContent(); // zárolt gép → némán nem küld

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == uname && u.IsActive, ct);
    if (user is not null && string.Equals(user.Email, mail, StringComparison.OrdinalIgnoreCase))
    {
        var code = SetResetCode(user);
        await db.SaveChangesAsync(ct);
        await EmailResetCodeAsync(email, user, code, ct);
        await AuditAsync(db, ctx, "password-code-requested", null, null, actorOverride: user.Username);
    }
    else
    {
        // Nem stimmel (rossz e-mail vagy nincs ilyen user) → naplózott sikertelen próba + counter.
        await RegisterLoginFailAsync(db, email, ctx, device, uname, "password-code-failed",
            user is null ? L.Program_001 : L.Program_027, ct);
    }
    // A válasz mindig OK (anti-enumeration).
    return Results.NoContent();
});

// Új jelszó beállítása a kapott kóddal.
app.MapPost("/auth/password/reset", async (HttpContext ctx, AppDbContext db, AuthService auth, IEmailSender email, CancellationToken ct) =>
{
    PasswordResetRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.PasswordResetRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Code))
        return Results.Json(new AuthError { Error = "invalid_code" }, AgentJsonContext.Default.AuthError, statusCode: 400);
    if ((req.NewPassword?.Length ?? 0) < 10)
        return Results.Json(new AuthError { Error = "weak_password" }, AgentJsonContext.Default.AuthError, statusCode: 400);

    var device = await FindDeviceAsync(db, req.DeviceId, ct);
    if (device?.LoginLockedAt is not null)
        return Results.Json(new AuthError { Error = "device_locked" }, AgentJsonContext.Default.AuthError, statusCode: 403);

    var uname = req.Username.Trim();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == uname && u.IsActive, ct);
    if (user is null || user.ResetCodeHash is null || user.ResetCodeExpiresAt is null
        || user.ResetCodeExpiresAt < DateTimeOffset.UtcNow
        || !string.Equals(user.ResetCodeHash, Sha256Hex(req.Code.Trim().ToUpperInvariant()), StringComparison.Ordinal))
    {
        await RegisterLoginFailAsync(db, email, ctx, device, uname, "password-reset-failed",
            user is null ? L.Program_001 : L.Program_005, ct);
        return Results.Json(new AuthError { Error = "invalid_code" }, AgentJsonContext.Default.AuthError, statusCode: 400);
    }

    user.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
    user.MustChangePassword = false;          // a user maga állította be
    user.PasswordChangedAt = DateTimeOffset.UtcNow;
    user.ResetCodeHash = null; user.ResetCodeExpiresAt = null;
    await auth.RevokeAllForUserAsync(user.Id, ct);
    await db.SaveChangesAsync(ct);
    await ResetLoginFailAsync(db, device, ct); // sikeres self-reset → számláló nullázása
    await AuditAsync(db, ctx, "user-password-reset-self", null, null, actorOverride: user.Username);
    return Results.NoContent();
});

app.MapPost("/auth/totp/confirm", async (HttpContext ctx, AppDbContext db, AuthService auth, SecretProtector protector, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    TotpConfirmRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.TotpConfirmRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }

    var user = await db.Users.FirstAsync(u => u.Id == v.Value.User.Id, ct);
    var secret = protector.TryUnprotect(user.TotpSecret);
    if (secret is null || req is null || !TotpService.Verify(secret, req.Code))
        return Results.Json(new AuthError { Error = "totp_invalid" }, AgentJsonContext.Default.AuthError, statusCode: 400);

    user.TotpConfirmed = true;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapPost("/auth/logout", async (HttpContext ctx, AuthService auth, CancellationToken ct) =>
{
    await auth.RevokeAsync(BearerToken(ctx), ct);
    return Results.NoContent();
});

app.MapGet("/auth/me", async (HttpContext ctx, AuthService auth, CancellationToken ct) =>
{
    var v = await auth.ValidateAsync(BearerToken(ctx), ct);
    if (v is null) return Results.Json(new AuthError { Error = "unauthorized" }, AgentJsonContext.Default.AuthError, statusCode: 401);
    var u = v.Value.User;
    return Results.Json(new MeResponse
    {
        Username = u.Username, Role = AuthService.RoleOf(u),
        MustChangePassword = u.MustChangePassword, TotpConfirmed = u.TotpConfirmed,
    }, AgentJsonContext.Default.MeResponse);
});

// === Agent WSS parancscsatorna ===
// Az agent ide tartja a kimenő kapcsolatot; a szerver ezen push-ol aláírt parancsot.
// Éles üzemben nginx terminálja a TLS-t és validálja a kliens-certet (mTLS),
// a device-azonosító a cert CN-jéből jön. Cert nélkül a ?deviceId= a fallback (dev).
app.Map("/agent", async (HttpContext ctx, AgentConnectionRegistry registry, AccessResultStore accessResults, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var log = lf.CreateLogger("AgentChannel");
    var deviceId = ResolveDeviceId(ctx);
    if (deviceId is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    registry.Register(deviceId, socket);
    log.LogInformation("Agent csatlakozott: {Device}", deviceId);

    // A csatlakozáskor a függő (Queued) parancsok kézbesítése — rövid scope-ban,
    // hogy ne tartsuk a DbContextet a teljes kapcsolat élettartamán át.
    using (var scope = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope())
    {
        var commands = scope.ServiceProvider.GetRequiredService<CommandService>();
        await commands.DrainQueuedAsync(deviceId, ctx.RequestAborted);
    }

    try
    {
        var scopes = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
        await PumpIncomingAsync(socket, deviceId, accessResults, scopes, log, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { /* leállás/lekapcsolódás */ }
    catch (WebSocketException ex) { log.LogDebug(ex, L.Program_006, deviceId); }
    finally
    {
        registry.Unregister(deviceId, socket);
        log.LogInformation(L.Program_007, deviceId);
    }
});

// === Telemetria ingest (éles: mTLS az nginx mögött) ===
app.MapPost("/api/telemetry", async (HttpContext ctx, ITelemetrySink sink) =>
{
    var deviceId = ResolveDeviceId(ctx) ?? "unknown";
    TelemetryPayload? payload;
    try
    {
        payload = await JsonSerializer.DeserializeAsync(
            ctx.Request.Body, AgentJsonContext.Default.TelemetryPayload, ctx.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }

    if (payload is null) return Results.BadRequest();
    await sink.IngestAsync(deviceId, payload, PublicIpOf(ctx), ctx.RequestAborted);
    return Results.NoContent();
});

// === A gép jelenti a VNC-jelszavát (mTLS) → devices.vnc_secret ===
app.MapPost("/api/vnc-secret", async (HttpContext ctx, AppDbContext db, SecretProtector protector) =>
{
    var deviceId = ResolveDeviceId(ctx);
    if (deviceId is null) return Results.Unauthorized();

    VncSecretReport? report;
    try
    {
        report = await JsonSerializer.DeserializeAsync(
            ctx.Request.Body, AgentJsonContext.Default.VncSecretReport, ctx.RequestAborted);
    }
    catch (JsonException) { return Results.BadRequest(); }
    if (report is null || string.IsNullOrEmpty(report.Secret)) return Results.BadRequest();

    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ctx.RequestAborted);
    if (device is null) return Results.NotFound();

    device.VncSecret = protector.Protect(report.Secret); // nyugalmi titkosítás
    device.VncSecretUpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ctx.RequestAborted);
    return Results.NoContent();
});

// === Admin (ideiglenes; localhost-only az nginxen át — a client SSH-tunnelen éri el) ===

// Eszközlista a DB-ből (online a registryből, + a VNC-jelszó a client autoconnecthez).
app.MapGet("/admin/devices", async (HttpContext ctx, AppDbContext db, AgentConnectionRegistry registry, SecretProtector protector, AuthService auth, CancellationToken ct) =>
{
    var devices = await db.Devices.Include(d => d.Group).OrderBy(d => d.Hostname).ToListAsync(ct);

    // Operator csak a grantolt gépeket látja; admin mindent.
    var me = (User)ctx.Items["user"]!;
    if (!AuthService.IsAdmin(me))
    {
        var (gids, dids) = await auth.GrantsAsync(me.Id, ct);
        devices = devices.Where(d => AuthService.CanAccessDevice(d, gids, dids)).ToList();
    }
    var list = devices.Select(d => new DeviceInfo
    {
        DeviceId = d.DeviceId,
        Hostname = d.Hostname,
        Status = d.Status.ToString(),
        Online = registry.IsConnected(d.DeviceId),
        LastSeenAt = d.LastSeenAt,
        VncSecret = protector.TryUnprotect(d.VncSecret),
        GroupId = d.GroupId,
        GroupName = d.Group?.Name,
        UpdateAllowed = d.UpdateAllowed,
        Channel = d.Channel,
        UnattendedAllowed = d.UnattendedAllowed,
        ConsentRequired = d.ConsentRequired,
        AgentVersion = d.AgentVersion,
        HelperVersion = d.HelperVersion,
        VncVersion = d.VncVersion,
        ClientVersion = d.ClientVersion,
        OsVersion = d.OsVersion,
        AgentRestarts = d.AgentRestarts,
        LastIncident = d.LastIncident,
        VncLocked = d.VncLocked,
        BootTimeUtc = d.BootTimeUtc,
        IpAddress = d.IpAddress,
        PublicIpAddress = d.PublicIpAddress,
        WifiSsid = d.WifiSsid,
        VpnActive = d.VpnActive,
        LoggedInUser = d.LoggedInUser,
        LoginFailCount = d.LoginFailCount,
        LoginLocked = d.LoginLockedAt is not null,
        Note = protector.TryUnprotect(d.Note),
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListDeviceInfo);
});

// Eszköz admin-mezőinek módosítása (csoport, flagek, megjegyzés). A null mezők változatlanok.
app.MapPut("/admin/devices/{deviceId}", async (string deviceId, HttpContext ctx, AppDbContext db, SecretProtector protector) =>
{
    DeviceUpdate? upd;
    try { upd = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.DeviceUpdate, ctx.RequestAborted); }
    catch (JsonException) { return Results.BadRequest(); }
    if (upd is null) return Results.BadRequest();

    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ctx.RequestAborted);
    if (device is null) return Results.NotFound();

    if (upd.GroupId is not null) device.GroupId = upd.GroupId == Guid.Empty ? null : upd.GroupId;
    if (upd.UpdateAllowed is not null) device.UpdateAllowed = upd.UpdateAllowed.Value;
    if (upd.Channel is not null && (upd.Channel is "rtm" or "beta")) device.Channel = upd.Channel;
    if (upd.UnattendedAllowed is not null) device.UnattendedAllowed = upd.UnattendedAllowed;
    if (upd.ConsentRequired is not null) device.ConsentRequired = upd.ConsentRequired;
    if (upd.Note is not null) device.Note = upd.Note.Length == 0 ? null : protector.Protect(upd.Note);
    if (upd.Status is not null && Enum.TryParse<DeviceStatus>(upd.Status, ignoreCase: true, out var st)) device.Status = st;

    await db.SaveChangesAsync(ctx.RequestAborted);
    await AuditAsync(db, ctx, "device-update", device.Id, device.Hostname);
    return Results.NoContent();
});

// Csoportok listája + létrehozása.
app.MapGet("/admin/groups", async (AppDbContext db, CancellationToken ct) =>
{
    var groups = await db.DeviceGroups.OrderBy(g => g.Name).ToListAsync(ct);
    var list = groups.Select(g => new GroupInfo
    {
        Id = g.Id, Name = g.Name, ConsentRequired = g.ConsentRequired, UnattendedAllowed = g.UnattendedAllowed,
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListGroupInfo);
});

app.MapPost("/admin/groups", async (HttpContext ctx, AppDbContext db) =>
{
    GroupInfo? g;
    try { g = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.GroupInfo, ctx.RequestAborted); }
    catch (JsonException) { return Results.BadRequest(); }
    if (g is null || string.IsNullOrWhiteSpace(g.Name)) return Results.BadRequest();

    var group = new RemoteServer.Data.Entities.DeviceGroup
    {
        Name = g.Name, ConsentRequired = g.ConsentRequired, UnattendedAllowed = g.UnattendedAllowed,
    };
    db.DeviceGroups.Add(group);
    await db.SaveChangesAsync(ctx.RequestAborted);
    return Results.Json(new GroupInfo { Id = group.Id, Name = group.Name, ConsentRequired = group.ConsentRequired, UnattendedAllowed = group.UnattendedAllowed }, AgentJsonContext.Default.GroupInfo);
});

app.MapPut("/admin/groups/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    GroupInfo? upd;
    try { upd = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.GroupInfo, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (upd is null || string.IsNullOrWhiteSpace(upd.Name)) return Results.BadRequest();

    var group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();
    group.Name = upd.Name.Trim();
    group.ConsentRequired = upd.ConsentRequired;
    group.UnattendedAllowed = upd.UnattendedAllowed;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapDelete("/admin/groups/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    var group = await db.DeviceGroups.FirstOrDefaultAsync(g => g.Id == id, ct);
    if (group is null) return Results.NotFound();
    // A csoportban lévő gépek csoport nélkülivé válnak (nem töröljük őket).
    var devices = await db.Devices.Where(d => d.GroupId == id).ToListAsync(ct);
    foreach (var d in devices) d.GroupId = null;
    db.DeviceGroups.Remove(group);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapGet("/admin/devices/online", (AgentConnectionRegistry registry) => Results.Ok(registry.ConnectedDevices));

// Update-parancs: csak ha a gépen engedélyezett a frissítés (UpdateAllowed).
app.MapPost("/admin/devices/{deviceId}/update", async (
    string deviceId, HttpContext ctx, AppDbContext db, CommandService commands, CancellationToken ct) =>
{
    UpdateRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.UpdateRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.Sha256))
        return Results.BadRequest();

    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    if (!device.UpdateAllowed)
        return Results.Conflict(new { error = "update_not_allowed" });

    var data = new CommandData { UpdateVersion = req.Version, UpdateUrl = req.Url, UpdateSha256 = req.Sha256, UpdateTarget = req.Target };
    var cmd = await commands.EnqueueAsync(deviceId, CommandTypes.Update, data, createdBy: null, ct);
    return cmd is null ? Results.NotFound() : Results.Ok(new { deviceId, version = req.Version, target = req.Target ?? "agent", status = cmd.Status.ToString() });
});

// Update-csomag feltöltése EGY CSATORNÁRA: a body a nyers exe; query: channel (rtm/beta),
// component (agent/updater), version. A szerver eltárolja, SHA-256-ot számol, és felveszi
// egy ReleasePackage sorba — onnantól ez a (csatorna, komponens) AKTUÁLIS csomagja.
app.MapPost("/admin/packages", async (HttpContext ctx, AppDbContext db, IOptions<ServerOptions> opt) =>
{
    var channel = Norm(ctx.Request.Query["channel"], "rtm");
    var component = Norm(ctx.Request.Query["component"], "agent");
    var version = ctx.Request.Query["version"].ToString();
    if (string.IsNullOrWhiteSpace(version)) return Results.BadRequest(new { error = "version_required" });
    if (channel is not ("rtm" or "beta")) return Results.BadRequest(new { error = "bad_channel" });
    if (component is not ("agent" or "updater" or "client" or "vnc")) return Results.BadRequest(new { error = "bad_component" });

    // Belső, ütközésmentes fájlnév: {component}-{channel}-{version}.{ext}. A vnc MSI-ként jön.
    var ext = component == "vnc" ? "msi" : "exe";
    var safeVer = version.Replace('/', '_').Replace('\\', '_');
    var fileName = $"{component}-{channel}-{safeVer}.{ext}";

    var dir = opt.Value.PackagesDir;
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, Path.GetFileName(fileName));

    await using (var fs = File.Create(path))
        await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);

    string sha;
    long size;
    await using (var read = File.OpenRead(path))
    {
        size = read.Length;
        sha = Convert.ToHexString(await SHA256.HashDataAsync(read, ctx.RequestAborted));
    }

    db.ReleasePackages.Add(new ReleasePackage
    {
        Channel = channel, Component = component, Version = version,
        FileName = fileName, Sha256 = sha, SizeBytes = size,
    });
    await db.SaveChangesAsync(ctx.RequestAborted);
    await AuditAsync(db, ctx, "package-upload", null, $"{component} · {channel} · {version}");

    return Results.Ok(new { channel, component, version, fileName, url = $"/api/updates/{fileName}", sha256 = sha });

    static string Norm(Microsoft.Extensions.Primitives.StringValues v, string dflt)
    {
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? dflt : s.Trim().ToLowerInvariant();
    }
});

// Csatornák aktuális csomagjai (komponensenként) — a kliens csatorna-nézetéhez.
app.MapGet("/admin/channels", async (AppDbContext db, CancellationToken ct) =>
{
    var all = await db.ReleasePackages.ToListAsync(ct);
    var current = all
        .GroupBy(p => new { p.Channel, p.Component })
        .Select(g => g.OrderByDescending(p => p.UploadedAt).First())
        .Select(p => new ChannelPackageInfo
        {
            Channel = p.Channel, Component = p.Component, Version = p.Version,
            FileName = p.FileName, Sha256 = p.Sha256, Url = $"/api/updates/{p.FileName}", UploadedAt = p.UploadedAt,
        })
        .OrderBy(p => p.Channel).ThenBy(p => p.Component)
        .ToList();
    return Results.Json(current, AgentJsonContext.Default.ListChannelPackageInfo);
});

// Rollout: egy csatorna AKTUÁLIS csomagját kiadja minden ott lévő, frissíthető, jóváhagyott gépnek.
app.MapPost("/admin/channels/{channel}/rollout", async (
    string channel, string? component, HttpContext ctx, AppDbContext db, CommandService commands, CancellationToken ct) =>
{
    channel = channel.Trim().ToLowerInvariant();
    var comp = string.IsNullOrWhiteSpace(component) ? "agent" : component.Trim().ToLowerInvariant();

    var pkg = await db.ReleasePackages.Where(p => p.Channel == channel && p.Component == comp)
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (pkg is null) return Results.NotFound(new { error = "no_current_package" });

    var devices = await db.Devices
        .Where(d => d.Channel == channel && d.UpdateAllowed && d.Status == DeviceStatus.Approved)
        .ToListAsync(ct);

    int sent = 0, skipped = 0;
    foreach (var d in devices)
    {
        // Már a cél-verzión van? (laza egyezés: a riportolt "2.0.0.0" kezdődik a "2.0.0"-val)
        var reported = comp switch { "updater" => d.HelperVersion, "vnc" => d.VncVersion, "client" => d.ClientVersion, _ => d.AgentVersion };
        if (reported is not null && reported.StartsWith(pkg.Version, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

        var data = new CommandData
        {
            UpdateVersion = pkg.Version, UpdateUrl = $"/api/updates/{pkg.FileName}",
            UpdateSha256 = pkg.Sha256, UpdateTarget = comp,
        };
        var cmd = await commands.EnqueueAsync(d.DeviceId, CommandTypes.Update, data, createdBy: null, ct);
        if (cmd is not null) sent++;
    }
    await AuditAsync(db, ctx, "rollout", null, L.Format(L.Program_008, comp, channel, pkg.Version, sent));
    return Results.Ok(new { channel, component = comp, version = pkg.Version, devices = devices.Count, sent, skipped });
});

// Promótálás: egy csatorna aktuális csomagját a cél-csatorna aktuálisává teszi (UGYANAZ a fájl, nincs újra-feltöltés).
app.MapPost("/admin/channels/{channel}/promote", async (
    string channel, string? component, string? to, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var from = channel.Trim().ToLowerInvariant();
    var comp = string.IsNullOrWhiteSpace(component) ? "agent" : component.Trim().ToLowerInvariant();
    var toChannel = string.IsNullOrWhiteSpace(to) ? "rtm" : to.Trim().ToLowerInvariant();
    if (toChannel is not ("rtm" or "beta")) return Results.BadRequest(new { error = "bad_channel" });

    var pkg = await db.ReleasePackages.Where(p => p.Channel == from && p.Component == comp)
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (pkg is null) return Results.NotFound(new { error = "no_current_package" });

    db.ReleasePackages.Add(new ReleasePackage
    {
        Channel = toChannel, Component = comp, Version = pkg.Version,
        FileName = pkg.FileName, Sha256 = pkg.Sha256, SizeBytes = pkg.SizeBytes,
    });
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "promote", null, $"{comp} · {from} → {toChannel} · {pkg.Version}");
    return Results.Ok(new { promoted = pkg.Version, component = comp, from, to = toChannel, fileName = pkg.FileName });
});

// Egy gép frissítése a SAJÁT csatornája aktuális csomagjára (per-device, csatorna-tudatos).
app.MapPost("/admin/devices/{deviceId}/update-channel", async (
    string deviceId, string? component, AppDbContext db, CommandService commands, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    if (!device.UpdateAllowed) return Results.Conflict(new { error = "update_not_allowed" });

    var comp = string.IsNullOrWhiteSpace(component) ? "agent" : component.Trim().ToLowerInvariant();
    var pkg = await db.ReleasePackages.Where(p => p.Channel == device.Channel && p.Component == comp)
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (pkg is null) return Results.NotFound(new { error = "no_current_package" });

    var data = new CommandData
    {
        UpdateVersion = pkg.Version, UpdateUrl = $"/api/updates/{pkg.FileName}",
        UpdateSha256 = pkg.Sha256, UpdateTarget = comp,
    };
    var cmd = await commands.EnqueueAsync(deviceId, CommandTypes.Update, data, createdBy: null, ct);
    return cmd is null ? Results.NotFound() : Results.Ok(new { deviceId, channel = device.Channel, version = pkg.Version, status = cmd.Status.ToString() });
});

// Update-csomag kiszolgálása (mTLS mögött az /api/ blokkban — csak beléptetett agentek).
app.MapGet("/api/updates/{fileName}", (string fileName, IOptions<ServerOptions> opt) =>
{
    var safe = Path.GetFileName(fileName);
    var path = Path.Combine(opt.Value.PackagesDir, safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream", safe) : Results.NotFound();
});

// Tunnel nyitása: a gép STABIL portját használja (enrollkor kiosztva); felülírható a query-ből.
app.MapPost("/admin/devices/{deviceId}/open-tunnel", async (
    string deviceId, int? remotePort, HttpContext ctx, AppDbContext db, CommandService commands, AuthService auth, AccessResultStore accessResults, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();

    // Operator csak a grantolt gépre indíthat tunnelt.
    var me = (User)ctx.Items["user"]!;
    if (!AuthService.IsAdmin(me))
    {
        var (gids, dids) = await auth.GrantsAsync(me.Id, ct);
        if (!AuthService.CanAccessDevice(device, gids, dids))
            return Results.Json(new AuthError { Error = "forbidden" }, AgentJsonContext.Default.AuthError, statusCode: 403);
    }

    var port = remotePort is > 0 ? remotePort.Value
             : device.TunnelPort ?? Random.Shared.Next(50000, 60000); // fallback régi (port nélküli) géphez

    // Effektív hozzáférés-policy: gép-szintű override, különben a csoport, különben alapértelmezés
    // (nincs consent / unattended engedett). Az agent ez alapján kérdez/dönt a tunnel-nyitás előtt.
    var grp = device.GroupId is { } gid ? await db.DeviceGroups.FirstOrDefaultAsync(x => x.Id == gid, ct) : null;
    bool consentRequired = device.ConsentRequired ?? grp?.ConsentRequired ?? false;
    bool unattendedAllowed = device.UnattendedAllowed ?? grp?.UnattendedAllowed ?? true;

    var cmd = await commands.EnqueueAsync(
        deviceId, CommandTypes.OpenTunnel,
        new CommandData { RemotePort = port, ConsentRequired = consentRequired, UnattendedAllowed = unattendedAllowed },
        createdBy: null, ct);
    if (cmd is null) return Results.NotFound();

    // A nonce-hoz kötjük: ki (Actor) melyik gépre kért hozzáférést → az agent kimenetelekor naplózzuk.
    accessResults.SetPending(cmd.Nonce ?? "", me.Username, device.Id, device.Hostname);

    return Results.Json(
        new OpenTunnelResult { DeviceId = deviceId, RemotePort = port, Status = cmd.Status.ToString(), Nonce = cmd.Nonce ?? "" },
        AgentJsonContext.Default.OpenTunnelResult);
});

// A hozzáférés-kérés eredménye (a konzol pollozza nonce alapján a tunnel-nyitás után).
// Üres outcome = még nincs válasz (az agent dolgozik / a felhasználót kérdezi).
app.MapGet("/admin/devices/access-result/{nonce}", (string nonce, AccessResultStore accessResults) =>
    Results.Json(new AccessResultInfo { Outcome = accessResults.Get(nonce) ?? "" }, AgentJsonContext.Default.AccessResultInfo));

// Napló (audit) lekérdezés szűrőkkel: action (kulcs), actor (felhasználónév), deviceId (gép), limit.
app.MapGet("/admin/audit", async (string? action, string? actor, string? deviceId, int? limit, AppDbContext db, CancellationToken ct) =>
{
    Guid? devGuid = null;
    if (!string.IsNullOrWhiteSpace(deviceId))
        devGuid = await db.Devices.Where(d => d.DeviceId == deviceId).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);

    var q = db.AuditLogs.AsQueryable();
    if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
    if (!string.IsNullOrWhiteSpace(actor)) q = q.Where(a => a.Actor == actor);
    if (devGuid is { } dg) q = q.Where(a => a.TargetDeviceId == dg);

    var rows = await q.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(limit ?? 200, 1, 1000)).ToListAsync(ct);

    var devIds = rows.Where(r => r.TargetDeviceId != null).Select(r => r.TargetDeviceId!.Value).Distinct().ToList();
    var hosts = devIds.Count == 0 ? new Dictionary<Guid, string>()
        : await db.Devices.Where(d => devIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Hostname, ct);

    var list = rows.Select(a => new AuditEntryInfo
    {
        CreatedAt = a.CreatedAt, Actor = a.Actor, Action = a.Action,
        Target = a.TargetDeviceId is { } t && hosts.TryGetValue(t, out var h) ? h : null,
        Detail = a.DetailJson,
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListAuditEntryInfo);
});

// === Beléptetés ===
// A gép CSR-t + tokent küld; siker esetén aláírt cert + CA jön vissza.
// Hiba esetén gép-olvasható kód (a kliens lokalizál belőle).
app.MapPost("/enroll", async (HttpContext ctx, EnrollmentService enroll, CancellationToken ct) =>
{
    EnrollRequest? req;
    try
    {
        req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.EnrollRequest, ct);
    }
    catch (JsonException)
    {
        return Results.Json(new EnrollError { Code = "invalid_request" }, AgentJsonContext.Default.EnrollError, statusCode: 400);
    }
    if (req is null)
        return Results.Json(new EnrollError { Code = "invalid_request" }, AgentJsonContext.Default.EnrollError, statusCode: 400);

    var result = await enroll.EnrollWithTokenAsync(req, ct);
    return result.Response is not null
        ? Results.Json(result.Response, AgentJsonContext.Default.EnrollResponse)
        : Results.Json(new EnrollError { Code = result.ErrorCode! }, AgentJsonContext.Default.EnrollError, statusCode: 400);
});

// === Token-gyártás (ideiglenes; később auth+2FA mögé) ===
app.MapPost("/admin/tokens", async (EnrollmentService enroll, int? maxUses, int? expiresInHours, CancellationToken ct) =>
{
    var (raw, _) = await enroll.CreateTokenAsync(maxUses ?? 1, expiresInHours, groupId: null, note: null, ct);
    return Results.Ok(new { token = raw, maxUses = maxUses ?? 1, expiresInHours });
});

// === Bootstrap blob: token nélküli ön-telepítéshez (site-token + szerver-URL egy stringben) ===
// A létrejövő token AutoApprove=false → a vele beléptetett gép Pending-be kerül (jóváhagyásra vár).
app.MapPost("/admin/bootstrap", async (
    HttpContext ctx, AppDbContext db, EnrollmentService enroll, IOptions<ServerOptions> opt,
    string? serverUrl, Guid? groupId, int? maxUses, int? expiresInHours, CancellationToken ct) =>
{
    var url = !string.IsNullOrWhiteSpace(serverUrl) ? serverUrl : opt.Value.PublicUrl;
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "no_server_url", hint = L.Program_009 });

    var (raw, _) = await enroll.CreateTokenAsync(maxUses ?? 100000, expiresInHours, groupId, note: "bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = raw });
    await AuditAsync(db, ctx, "bootstrap-create", null, $"max {(maxUses ?? 100000)}");
    return Results.Ok(new { blob, url = url.TrimEnd('/'), token = raw, groupId, maxUses = maxUses ?? 100000, expiresInHours });
});

// === Bootstrap/beléptető tokenek (blob-ok) listája + visszavonás/törlés ===
app.MapGet("/admin/tokens-list", async (AppDbContext db, CancellationToken ct) =>
{
    var groups = await db.DeviceGroups.ToDictionaryAsync(g => g.Id, g => g.Name, ct);
    var tokens = await db.EnrollmentTokens.OrderByDescending(t => t.CreatedAt).Take(500).ToListAsync(ct);
    var list = tokens.Select(t => new BootstrapTokenInfo
    {
        Id = t.Id, GroupId = t.GroupId, GroupName = t.GroupId is { } g && groups.TryGetValue(g, out var n) ? n : null,
        MaxUses = t.MaxUses, UseCount = t.UseCount, AutoApprove = t.AutoApprove,
        CreatedAt = t.CreatedAt, ExpiresAt = t.ExpiresAt, RevokedAt = t.RevokedAt, LastUsedAt = t.UsedAt, Note = t.Note,
        MsiFileName = t.MsiFileName,
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListBootstrapTokenInfo);
});

app.MapPost("/admin/tokens-list/{id:guid}/revoke", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (token is null) return Results.NotFound();
    token.RevokedAt ??= DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "token-revoke", null, id.ToString("N")[..8]);
    return Results.NoContent();
});

// Blob/token utólagos módosítása: max telepítés és/vagy lejárat. A letiltás = revoke (külön),
// ezért a MaxUses NEM mehet a már elhasznált (UseCount) alá.
app.MapPut("/admin/tokens-list/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    EditTokenRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.EditTokenRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null) return Results.BadRequest();

    var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (token is null) return Results.NotFound();

    if (req.MaxUses is { } mu)
    {
        if (mu < token.UseCount)
            return Results.BadRequest(new { error = "max_below_used", useCount = token.UseCount });
        token.MaxUses = mu;
    }
    if (req.ClearExpiry) token.ExpiresAt = null;
    else if (req.ExpiresInHours is { } h) token.ExpiresAt = DateTimeOffset.UtcNow.AddHours(h);

    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "token-edit", null, id.ToString("N")[..8]);
    return Results.NoContent();
});

app.MapDelete("/admin/tokens-list/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var token = await db.EnrollmentTokens.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (token is null) return Results.NotFound();
    db.EnrollmentTokens.Remove(token);
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "token-delete", null, id.ToString("N")[..8]);
    return Results.NoContent();
});

// === Szerver-beállítások: branding (tulajdonos + support) + e-mail küldés ===
app.MapGet("/admin/settings", async (AppDbContext db, CancellationToken ct) =>
{
    var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
    var info = new ServerSettingsInfo();
    if (s is not null)
    {
        info.OwnerName = s.OwnerName; info.SupportPhone = s.SupportPhone; info.SupportEmail = s.SupportEmail;
        info.EmailProvider = s.EmailProvider;
        info.SmtpHost = s.SmtpHost; info.SmtpPort = s.SmtpPort; info.SmtpUseTls = s.SmtpUseTls;
        info.SmtpUser = s.SmtpUser; info.SmtpFrom = s.SmtpFrom;
        info.HasSmtpPassword = !string.IsNullOrEmpty(s.SmtpPasswordEnc);
        info.GraphTenantId = s.GraphTenantId; info.GraphClientId = s.GraphClientId; info.GraphSender = s.GraphSender;
        info.HasGraphSecret = !string.IsNullOrEmpty(s.GraphClientSecretEnc);
        info.GraphSecretExpiresAt = s.GraphSecretExpiresAt;
    }
    return Results.Json(info, AgentJsonContext.Default.ServerSettingsInfo);
});

app.MapPut("/admin/settings", async (HttpContext ctx, AppDbContext db, SecretProtector protector, CancellationToken ct) =>
{
    var upd = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.ServerSettingsInfo, ct);
    if (upd is null) return Results.BadRequest();

    var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
    if (s is null) { s = new ServerSettings(); db.ServerSettings.Add(s); }

    s.OwnerName = Nz(upd.OwnerName); s.SupportPhone = Nz(upd.SupportPhone); s.SupportEmail = Nz(upd.SupportEmail);
    s.EmailProvider = string.IsNullOrWhiteSpace(upd.EmailProvider) ? "none" : upd.EmailProvider.Trim().ToLowerInvariant();
    s.SmtpHost = Nz(upd.SmtpHost); s.SmtpPort = upd.SmtpPort <= 0 ? 587 : upd.SmtpPort; s.SmtpUseTls = upd.SmtpUseTls;
    s.SmtpUser = Nz(upd.SmtpUser); s.SmtpFrom = Nz(upd.SmtpFrom);
    if (!string.IsNullOrEmpty(upd.SmtpPassword)) s.SmtpPasswordEnc = protector.Protect(upd.SmtpPassword);
    s.GraphTenantId = Nz(upd.GraphTenantId); s.GraphClientId = Nz(upd.GraphClientId); s.GraphSender = Nz(upd.GraphSender);
    if (!string.IsNullOrEmpty(upd.GraphClientSecret)) s.GraphClientSecretEnc = protector.Protect(upd.GraphClientSecret);

    // Secret lejárat: max 2 év a mostantól. Ha változott a lejárat → újra figyelmeztethetünk.
    var prevExpiry = s.GraphSecretExpiresAt;
    if (upd.GraphSecretExpiresAt is { } exp)
    {
        var max = DateTimeOffset.UtcNow.AddYears(2);
        s.GraphSecretExpiresAt = exp > max ? max : exp;
    }
    else s.GraphSecretExpiresAt = null;
    if (s.GraphSecretExpiresAt != prevExpiry) s.SecretExpiryNotifiedAt = null;

    await db.SaveChangesAsync(ct);

    // Napló-detail: a mentett (nem-titkos) értékek pillanatképe; titkoknál csak a tény, az érték SOHA.
    var detail = $"provider={s.EmailProvider}; owner={Q(s.OwnerName)}; phone={Q(s.SupportPhone)}; email={Q(s.SupportEmail)}";
    if (s.EmailProvider == "smtp")
        detail += $"; smtp={Q(s.SmtpHost)}:{s.SmtpPort} tls={s.SmtpUseTls} user={Q(s.SmtpUser)} from={Q(s.SmtpFrom)}";
    if (s.EmailProvider == "graph")
        detail += $"; graph tenant={Q(s.GraphTenantId)} client={Q(s.GraphClientId)} sender={Q(s.GraphSender)} expires={s.GraphSecretExpiresAt:yyyy-MM-dd}";
    if (!string.IsNullOrEmpty(upd.SmtpPassword)) detail += L.Program_010;
    if (!string.IsNullOrEmpty(upd.GraphClientSecret)) detail += L.Program_011;

    await AuditAsync(db, ctx, "settings-update", null, detail);
    return Results.NoContent();

    static string? Nz(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    static string Q(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v;
});

app.MapPost("/admin/settings/test-email", async (HttpContext ctx, AppDbContext db, IEmailSender email, CancellationToken ct) =>
{
    var req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.TestEmailRequest, ct);
    if (req is null || string.IsNullOrWhiteSpace(req.To)) return Results.BadRequest(new { error = "no_recipient" });

    var (ok, err) = await email.SendAsync(req.To.Trim(),
        "RemoteAppClient teszt e-mail",
        L.Program_012, ct);
    await AuditAsync(db, ctx, "settings-test-email", null, ok ? req.To.Trim() : L.Format(L.Program_028, err));
    return ok ? Results.Ok(new { ok = true }) : Results.Problem(err ?? "send_failed");
});

// Publikus branding (a tunnelen át, bejelentkezés ELŐTT is) — az auth-gate kivételt ad rá.
app.MapGet("/admin/branding", async (AppDbContext db, CancellationToken ct) =>
{
    var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
    var b = new BrandingInfo { OwnerName = s?.OwnerName, SupportPhone = s?.SupportPhone, SupportEmail = s?.SupportEmail };
    return Results.Json(b, AgentJsonContext.Default.BrandingInfo);
});

// === MSI-gyártás: egy csoporthoz, egy csatorna aktuális exéiből (wixl) ===
// Lerakja az agent+updater exét + a group bootstrap.dat-ját, és install-service-szel telepít.
app.MapPost("/admin/msi", async (
    string? group, string? channel, bool? client, bool? shortcut, HttpContext ctx, AppDbContext db, EnrollmentService enroll,
    MsiBuilder msi, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    var ch = string.IsNullOrWhiteSpace(channel) ? "rtm" : channel.Trim().ToLowerInvariant();
    bool includeClient = client ?? true;       // alapból a konzol-klienst is telepíti
    bool startMenuShortcut = shortcut ?? true; // alapból Start menü parancsikonnal

    Guid? groupId = Guid.TryParse(group, out var g) ? g : null;
    string label = "all";
    if (groupId is not null)
    {
        var grp = await db.DeviceGroups.FirstOrDefaultAsync(x => x.Id == groupId.Value, ct);
        if (grp is null) return Results.NotFound(new { error = "group_not_found" });
        label = grp.Name;
    }

    var url = opt.Value.PublicUrl;
    if (string.IsNullOrWhiteSpace(url)) return Results.BadRequest(new { error = "no_server_url" });

    var agentPkg = await db.ReleasePackages.Where(p => p.Channel == ch && p.Component == "agent")
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (agentPkg is null) return Results.NotFound(new { error = "no_agent_package" });
    var updaterPkg = await db.ReleasePackages.Where(p => p.Channel == ch && p.Component == "updater")
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);

    var agentExe = Path.Combine(opt.Value.PackagesDir, agentPkg.FileName);
    if (!File.Exists(agentExe)) return Results.NotFound(new { error = "agent_file_missing" });
    var updaterExe = updaterPkg is null ? null : Path.Combine(opt.Value.PackagesDir, updaterPkg.FileName);
    if (updaterExe is not null && !File.Exists(updaterExe)) updaterExe = null;

    // Konzol-kliens (opcionális): az adott csatorna aktuális 'client' csomagja, ha kérve van + létezik.
    string? clientExe = null;
    if (includeClient)
    {
        var clientPkg = await db.ReleasePackages.Where(p => p.Channel == ch && p.Component == "client")
            .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
        if (clientPkg is not null)
        {
            var path = Path.Combine(opt.Value.PackagesDir, clientPkg.FileName);
            if (File.Exists(path)) clientExe = path;
        }
    }

    // Csoport-bootstrap: AutoApprove=false site-token → a telepített gép Pending lesz.
    var (rawToken, tokenEntity) = await enroll.CreateTokenAsync(100000, expiresInHours: null, groupId, note: "msi-bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = rawToken });

    var ownerName = (await db.ServerSettings.FirstOrDefaultAsync(ct))?.OwnerName;
    var res = await msi.BuildAsync(agentExe, updaterExe, clientExe, blob, agentPkg.Version, label, startMenuShortcut, ownerName, ct);
    if (!res.Ok) return Results.Problem(res.Error ?? "msi_failed");

    // A blobot a generált MSI-hez kötjük → a bootstrap nézet megmutatja, melyik MSI-hez tartozik.
    tokenEntity.MsiFileName = res.FileName;
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "msi-build", null, $"{label} · {ch} · {agentPkg.Version}");

    return Results.Ok(new
    {
        fileName = res.FileName,
        url = $"/admin/msi/{res.FileName}",
        channel = ch, group = label, version = agentPkg.Version,
        includesUpdater = updaterExe is not null,
        includesClient = clientExe is not null,
    });
});

// MSI letöltése (admin/localhost — a kliens SSH-tunnelen éri el).
app.MapGet("/admin/msi/{fileName}", (string fileName, IOptions<ServerOptions> opt) =>
{
    var safe = Path.GetFileName(fileName);
    var path = Path.Combine(opt.Value.PackagesDir, safe);
    return File.Exists(path) ? Results.File(path, "application/x-msi", safe) : Results.NotFound();
});

// === User-kezelés (admin-only; az operatort a middleware már kizárta) ===
app.MapGet("/admin/users", async (AppDbContext db, CancellationToken ct) =>
{
    var users = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).OrderBy(u => u.Username).ToListAsync(ct);
    var hello = await db.HelloCredentials.Where(c => c.RevokedAt == null)
        .GroupBy(c => c.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
    var list = users.Select(u => new UserInfo
    {
        Id = u.Id, Username = u.Username, Name = u.Name, Email = u.Email, Role = AuthService.RoleOf(u),
        IsActive = u.IsActive, MustChangePassword = u.MustChangePassword, TotpConfirmed = u.TotpConfirmed, LastLoginAt = u.LastLoginAt,
        HelloCount = hello.GetValueOrDefault(u.Id),
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListUserInfo);
});

app.MapPost("/admin/users", async (HttpContext ctx, AppDbContext db, IEmailSender email, CancellationToken ct) =>
{
    CreateUserRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.CreateUserRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest(new { error = "username_required" });
    if (string.IsNullOrWhiteSpace(req.Email)) return Results.BadRequest(new { error = "email_required" });

    var role = req.Role == "admin" ? "admin" : "operator";
    if (await db.Users.AnyAsync(u => u.Username == req.Username, ct)) return Results.Conflict(new { error = "username_taken" });

    var temp = TempPw();
    var user = new User { Username = req.Username.Trim(), Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim(), Email = req.Email.Trim(), PasswordHash = PasswordHasher.Hash(temp), MustChangePassword = true };
    var code = SetResetCode(user);   // mindig adunk helyreállítási tokent (az admin felület ezt mutatja)
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    await SetRoleAsync(db, user.Id, role, ct);
    await db.SaveChangesAsync(ct);

    bool emailSent = req.EmailCode && await EmailResetCodeAsync(email, user, code, ct);

    await AuditAsync(db, ctx, "user-create", null, $"{user.Username} · {role}{(emailSent ? " · token e-mailben" : "")}");
    return Results.Json(new CreateUserResponse { Id = user.Id, Username = user.Username, TempPassword = temp, ResetCode = code, EmailSent = emailSent }, AgentJsonContext.Default.CreateUserResponse);
});

app.MapPut("/admin/users/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    UserUpdate? upd;
    try { upd = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.UserUpdate, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (upd is null) return Results.BadRequest();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null) return Results.NotFound();

    // Önkizárás-védelem: a hívó admin ne deaktiválja / ne fokozza le saját magát.
    var me = (User)ctx.Items["user"]!;
    if (id == me.Id && upd.IsActive == false) return Results.BadRequest(new { error = "self_deactivate" });
    if (id == me.Id && upd.Role == "operator") return Results.BadRequest(new { error = "self_demote" });

    if (upd.Role is "admin" or "operator") await SetRoleAsync(db, id, upd.Role, ct);
    if (upd.Name is not null) user.Name = upd.Name.Trim().Length == 0 ? null : upd.Name.Trim();
    if (upd.Email is not null) user.Email = upd.Email.Trim().Length == 0 ? null : upd.Email.Trim();
    if (upd.IsActive is { } act)
    {
        user.IsActive = act;
        if (!act) await auth.RevokeAllForUserAsync(id, ct); // deaktiválás = azonnali kiléptetés
    }
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "user-update", null, user.Username);
    return Results.NoContent();
});

// Gép belépés-zárolásának feloldása (admin): számláló nullázása + zár törlése.
app.MapPost("/admin/devices/{deviceId}/unlock", async (string deviceId, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    device.LoginFailCount = 0;
    device.LoginLockedAt = null;
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "device-unlock", device.Id, device.Hostname);
    return Results.NoContent();
});

app.MapPost("/admin/users/{id:guid}/reset-password", async (Guid id, bool? emailCode, bool? clearTotp, HttpContext ctx, AppDbContext db, AuthService auth, IEmailSender email, CancellationToken ct) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null) return Results.NotFound();

    var temp = TempPw();
    user.PasswordHash = PasswordHasher.Hash(temp);
    user.MustChangePassword = true;
    var code = SetResetCode(user);   // a régi jelszót érvényteleníti; a user a tokennel áll be új jelszót
    if (clearTotp == true) { user.TotpSecret = null; user.TotpConfirmed = false; } // elveszett authenticator → újra-enroll
    await auth.RevokeAllForUserAsync(id, ct);
    await db.SaveChangesAsync(ct);

    bool emailSent = emailCode == true && await EmailResetCodeAsync(email, user, code, ct);

    await AuditAsync(db, ctx, "user-reset-password", null, $"{user.Username}{(emailSent ? " · token e-mailben" : "")}{(clearTotp == true ? " · TOTP törölve" : "")}");
    return Results.Json(new CreateUserResponse { Id = user.Id, Username = user.Username, TempPassword = temp, ResetCode = code, EmailSent = emailSent }, AgentJsonContext.Default.CreateUserResponse);
});

// TOTP törlése ÖNMAGÁBAN (jelszó-reset nélkül): elveszett authenticator esetén újra-enroll.
app.MapPost("/admin/users/{id:guid}/clear-totp", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null) return Results.NotFound();
    user.TotpSecret = null;
    user.TotpConfirmed = false;
    await auth.RevokeAllForUserAsync(id, ct); // kiléptetés → a következő belépéskor újra beállítja
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "user-totp-clear", null, user.Username);
    return Results.NoContent();
});

app.MapPost("/admin/users/{id:guid}/revoke-sessions", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    // Önkizárás-védelem: ne tudja a hívó admin saját magát azonnal kiléptetni.
    var me = (User)ctx.Items["user"]!;
    if (id == me.Id) return Results.BadRequest(new { error = "self_revoke" });
    var uname = (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct))?.Username ?? id.ToString();
    await auth.RevokeAllForUserAsync(id, ct);
    await AuditAsync(db, ctx, "user-revoke-sessions", null, uname);
    return Results.NoContent();
});

// Egy user Windows Hello eszközei (admin): listázás + visszavonás.
app.MapGet("/admin/users/{id:guid}/hello", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return Results.NotFound();
    var list = await db.HelloCredentials.Where(c => c.UserId == id && c.RevokedAt == null)
        .OrderByDescending(c => c.CreatedAt)
        .Select(c => new HelloCredentialInfo { Id = c.Id, DeviceName = c.DeviceName, CreatedAt = c.CreatedAt, LastUsedAt = c.LastUsedAt })
        .ToListAsync(ct);
    return Results.Json(list, AgentJsonContext.Default.ListHelloCredentialInfo);
});

app.MapPost("/admin/users/{id:guid}/hello/{credId:guid}/revoke", async (Guid id, Guid credId, AppDbContext db, CancellationToken ct) =>
{
    var cred = await db.HelloCredentials.FirstOrDefaultAsync(c => c.Id == credId && c.UserId == id, ct);
    if (cred is null) return Results.NotFound();
    cred.RevokedAt ??= DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapGet("/admin/users/{id:guid}/grants", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    var grants = await db.UserGrants.Where(g => g.UserId == id).ToListAsync(ct);
    var groupIds = grants.Where(g => g.GroupId != null).Select(g => g.GroupId!.Value).ToList();
    var devIds = grants.Where(g => g.DeviceId != null).Select(g => g.DeviceId!.Value).ToList();
    var groups = (await db.DeviceGroups.Where(g => groupIds.Contains(g.Id)).ToListAsync(ct)).ToDictionary(g => g.Id);
    var devices = (await db.Devices.Where(d => devIds.Contains(d.Id)).ToListAsync(ct)).ToDictionary(d => d.Id);

    var list = grants.Select(g => new GrantInfo
    {
        Id = g.Id,
        GroupId = g.GroupId,
        GroupName = g.GroupId is { } gg && groups.TryGetValue(gg, out var grp) ? grp.Name : null,
        DeviceId = g.DeviceId is { } dd && devices.TryGetValue(dd, out var dev) ? dev.DeviceId : null,
        DeviceHostname = g.DeviceId is { } dh && devices.TryGetValue(dh, out var dv) ? dv.Hostname : null,
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListGrantInfo);
});

app.MapPost("/admin/users/{id:guid}/grants", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    GrantRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.GrantRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null) return Results.BadRequest();
    if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return Results.NotFound();

    Guid? devicePk = null;
    if (!string.IsNullOrWhiteSpace(req.DeviceId))
    {
        var dev = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == req.DeviceId, ct);
        if (dev is null) return Results.NotFound(new { error = "device_not_found" });
        devicePk = dev.Id;
    }
    if (req.GroupId is null && devicePk is null) return Results.BadRequest(new { error = "group_or_device_required" });

    db.UserGrants.Add(new UserGrant { UserId = id, GroupId = req.GroupId, DeviceId = devicePk });
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapDelete("/admin/users/{id:guid}/grants/{grantId:guid}", async (Guid id, Guid grantId, AppDbContext db, CancellationToken ct) =>
{
    var g = await db.UserGrants.FirstOrDefaultAsync(x => x.Id == grantId && x.UserId == id, ct);
    if (g is null) return Results.NotFound();
    db.UserGrants.Remove(g);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// === Bootstrap: szerepek (admin/operator) + első admin user (ideiglenes jelszó a szerver-logba) ===
using (var scope = app.Services.CreateScope())
{
    var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    foreach (var rn in new[] { "admin", "operator" })
        if (!await sdb.Roles.AnyAsync(r => r.Name == rn)) sdb.Roles.Add(new Role { Name = rn });
    await sdb.SaveChangesAsync();

    if (!await sdb.Users.AnyAsync())
    {
        var temp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var admin = new User { Username = "admin", PasswordHash = PasswordHasher.Hash(temp), MustChangePassword = true };
        sdb.Users.Add(admin);
        await sdb.SaveChangesAsync();
        sdb.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = (await sdb.Roles.FirstAsync(r => r.Name == "admin")).Id });
        await sdb.SaveChangesAsync();
        app.Logger.LogWarning(L.Program_013, temp);
    }
}

app.Run();

// Audit-bejegyzés írása (best-effort; az audit ne bontsa el a műveletet). Az actor a bejelentkezett user.
static async Task AuditAsync(AppDbContext db, HttpContext ctx, string action, Guid? target = null, string? detail = null, string? actorOverride = null)
{
    try
    {
        var actor = actorOverride ?? (ctx.Items["user"] as RemoteServer.Data.Entities.User)?.Username ?? "system";
        db.AuditLogs.Add(new RemoteServer.Data.Entities.AuditLog
        {
            Actor = actor, Action = action, TargetDeviceId = target, DetailJson = detail,
            Ip = ctx.Connection.RemoteIpAddress?.ToString(),
        });
        await db.SaveChangesAsync(ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        // Az audit hiba ne befolyásolja a választ — de legalább naplózzuk (eddig néma volt).
        try { ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Audit").LogWarning(ex, L.Program_014, action); } catch { }
    }
}

// A Bearer session-token kiolvasása az Authorization fejlécből.
static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..].Trim() : null;
}

// Min-verzió kapu: ha a kliens régebbi a megengedettnél, mustUpdate-választ ad a csatorna aktuális
// kliens-csomagjával (innen tölti le a kötelező frissítést). null = a kliens elég friss, mehet a login.
// Ha nincs kliens-csomag amire frissíteni lehetne, nem blokkolunk (különben kizárnánk a klienst).
static async Task<LoginResponse?> ClientUpdateGateAsync(
    string? clientVersion, string? channel, string minVersion, AppDbContext db, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(minVersion) || !Version.TryParse(minVersion, out var min)) return null;
    bool tooOld = !Version.TryParse(clientVersion, out var cv) || cv < min;
    if (!tooOld) return null;

    var ch = channel?.Trim().ToLowerInvariant();
    if (ch is not ("rtm" or "beta")) ch = "rtm";

    var pkg = await db.ReleasePackages.Where(p => p.Component == "client" && p.Channel == ch)
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct)
        ?? await db.ReleasePackages.Where(p => p.Component == "client" && p.Channel == "rtm")
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (pkg is null) return null; // nincs mire frissíteni → ne brickeljük a klienst

    return new LoginResponse
    {
        MustUpdate = true,
        UpdateVersion = pkg.Version,
        UpdateFileName = pkg.FileName,
        UpdateSha256 = pkg.Sha256,
    };
}

// Egy user szerepének beállítása (a meglévő szerepeket lecseréli). A hívó ment.
static async Task SetRoleAsync(AppDbContext db, Guid userId, string roleName, CancellationToken ct)
{
    var existing = await db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
    db.UserRoles.RemoveRange(existing);
    var role = await db.Roles.FirstAsync(r => r.Name == roleName, ct);
    db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
}

// Rövid, URL-biztos ideiglenes jelszó.
static string TempPw() =>
    Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

// 8 karakteres, könnyen begépelhető reset-kód (nincs 0/O/1/I, csupa nagybetű/szám).
static string ResetCode()
{
    const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    var bytes = RandomNumberGenerator.GetBytes(8);
    var sb = new System.Text.StringBuilder(8);
    foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
    return sb.ToString();
}

static string Sha256Hex(string s) =>
    Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

// === Gép-szintű belépés-zárolás (brute-force) ===
const int LoginFailLockThreshold = 5;

static async Task<RemoteServer.Data.Entities.Device?> FindDeviceAsync(AppDbContext db, string? deviceId, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(deviceId) ? null : await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);

/// <summary>
/// Sikertelen próba: NAPLÓZZA (a usernél is — actor=username, ha létezik —, ÉS a gépnél is — TargetDeviceId+detail),
/// növeli a gép számlálóját; az 5. próbánál zárol + e-mailt küld az adminnak.
/// </summary>
static async Task RegisterLoginFailAsync(AppDbContext db, IEmailSender email, HttpContext ctx,
    RemoteServer.Data.Entities.Device? device, string username, string action, string reason, CancellationToken ct)
{
    bool justLocked = false;
    if (device is not null)
    {
        device.LoginFailCount++;
        justLocked = device.LoginFailCount >= LoginFailLockThreshold && device.LoginLockedAt is null;
        if (justLocked) device.LoginLockedAt = DateTimeOffset.UtcNow;
    }

    // A próbát rögzítjük: actor=a próbált felhasználónév (létező usernél a saját logjában látszik),
    // TargetDeviceId=a gép (a gép logjában látszik), detail=mivel próbálkozott + ok. Ez menti a device-számlálót is.
    await AuditAsync(db, ctx, action, device?.Id, $"{username} · {reason}", actorOverride: username);

    if (justLocked && device is not null)
    {
        await AuditAsync(db, ctx, "device-locked", device.Id, L.Format(L.Program_015, LoginFailLockThreshold, username), actorOverride: "system");

        var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
        var to = s?.SupportEmail;
        if (!string.IsNullOrWhiteSpace(to))
        {
            var body = L.Format(L.Program_016, device.Hostname, LoginFailLockThreshold) +
                       L.Format(L.Program_017, username, PublicIpOf(ctx) ?? "-", DateTimeOffset.UtcNow) +
                       L.Program_018;
            await email.SendAsync(to!, L.Program_019, body, ct);
        }
    }
}

static async Task ResetLoginFailAsync(AppDbContext db, RemoteServer.Data.Entities.Device? device, CancellationToken ct)
{
    if (device is null || (device.LoginFailCount == 0 && device.LoginLockedAt is null)) return;
    device.LoginFailCount = 0;
    device.LoginLockedAt = null;
    await db.SaveChangesAsync(ct);
}

/// <summary>Jelszó-helyreállítási tokent állít a userre (hash + 30 perc lejárat). A nyers tokent adja vissza (NEM ment — a hívó ment).</summary>
static string SetResetCode(RemoteServer.Data.Entities.User user)
{
    var code = ResetCode();
    user.ResetCodeHash = Sha256Hex(code);
    user.ResetCodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    return code;
}

/// <summary>A helyreállítási tokent e-mailben kiküldi a usernek. true = sikerült.</summary>
static async Task<bool> EmailResetCodeAsync(IEmailSender email, RemoteServer.Data.Entities.User user, string code, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(user.Email)) return false;
    var body = L.Format(L.Program_020, user.Username, code) +
               L.Program_021 +
               L.Program_022;
    var (ok, _) = await email.SendAsync(user.Email!, L.Program_023, body, ct);
    return ok;
}

// A device-azonosító feloldása. Éles üzemben az nginx validálja a kliens-certet
// (mTLS) és a CN-t headerben adja át; a backend csak localhostról (nginx) érhető el,
// így a headerek megbízhatók. Fallback: közvetlen Kestrel-cert, majd ?deviceId= (dev).
static string? ResolveDeviceId(HttpContext ctx)
{
    if (string.Equals(ctx.Request.Headers["X-Client-Verify"], "SUCCESS", StringComparison.OrdinalIgnoreCase))
    {
        var cn = ExtractCn(ctx.Request.Headers["X-Client-Dn"].ToString());
        if (!string.IsNullOrWhiteSpace(cn)) return cn;
    }

    var cert = ctx.Connection.ClientCertificate;
    if (cert is not null)
    {
        var cn = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        if (!string.IsNullOrWhiteSpace(cn)) return cn;
    }

    var q = ctx.Request.Query["deviceId"].ToString();
    return string.IsNullOrWhiteSpace(q) ? null : q;
}

// Az agent publikus IP-je. Éles: az nginx X-Real-IP fejléce ($remote_addr = a közvetlen
// kapcsolat forrás-IP-je, vagyis ahonnan a tunnel jön). Fallback: X-Forwarded-For első
// eleme, majd a közvetlen Kestrel-kapcsolat (dev).
static string? PublicIpOf(HttpContext ctx)
{
    var real = ctx.Request.Headers["X-Real-IP"].ToString();
    if (!string.IsNullOrWhiteSpace(real)) return real.Trim();

    var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(xff))
        return xff.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null) return null;
    return (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
}

// CN kinyerése egy DN-ből (pl. "CN=abc123" vagy "/CN=abc123" vagy "CN=abc,O=...").
static string? ExtractCn(string dn)
{
    if (string.IsNullOrWhiteSpace(dn)) return null;
    foreach (var part in dn.Split([',', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            return part[3..].Trim();
    }
    return null;
}

// Bejövő üzenetek (ACK-ok, státusz) olvasása. Most logol; később a commands.result-ba megy.
static async Task PumpIncomingAsync(WebSocket socket, string deviceId, AccessResultStore accessResults, IServiceScopeFactory scopes, ILogger log, CancellationToken ct)
{
    var buffer = new byte[4096];
    var message = new MemoryStream();
    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        message.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                return;
            }
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        // Agent → szerver üzenet: jelenleg a tunnel-nyitás/hozzájárulás eredménye (nonce-hoz kötve).
        try
        {
            var msg = JsonSerializer.Deserialize(message.ToArray(), AgentJsonContext.Default.AgentUplinkMessage);
            if (msg is { Type: "access-result" } && !string.IsNullOrEmpty(msg.Nonce))
            {
                var entry = accessResults.RecordOutcome(msg.Nonce, msg.Outcome);
                log.LogInformation(L.Program_024, deviceId, msg.Outcome, msg.Nonce);

                // Naplózás: a kimenetelt audit-sorrá írjuk (ki, melyik gép, eredmény).
                var action = msg.Outcome switch
                {
                    "granted" => "connect",        // a felhasználó kifejezetten engedélyezte (Igen)
                    "auto" => "connect-auto",       // hozzájárulás NÉLKÜL (consent kikapcsolva vagy nincs jelen user)
                    "denied" => "access-denied",
                    "timeout" => "access-timeout",
                    "no-user" => "access-no-user",
                    "locked" => "access-locked",
                    _ => "access-" + msg.Outcome,
                };
                try
                {
                    using var scope = scopes.CreateScope();
                    var adb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    adb.AuditLogs.Add(new AuditLog
                    {
                        Actor = entry?.Actor ?? "?",
                        Action = action,
                        TargetDeviceId = entry?.DeviceId,
                        // ha nincs Guid (ritka), a hostname fallback a Detailben; egyébként null (a Target hordozza)
                        DetailJson = entry?.DeviceId is null && !string.IsNullOrEmpty(entry?.Hostname) ? entry!.Hostname : null,
                    });
                    await adb.SaveChangesAsync(ct);
                }
                catch (Exception ex) { log.LogWarning(ex, L.Program_025); }
            }
        }
        catch (JsonException) { log.LogDebug(L.Program_026, deviceId); }
    }
}
