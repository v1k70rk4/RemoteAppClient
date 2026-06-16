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

// Update packages (agent exe) are about 70-100 MB, so raise Kestrel's default ~28 MB limit.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 512L * 1024 * 1024);

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

// MariaDB (Galera) with EF Core 9 + Pomelo. Retry handles Galera transient
// Avoids swallowing certification/deadlock errors. Connection string comes from env/secret.
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
builder.Services.AddHostedService<CommandExpiryWatcher>();

var app = builder.Build();
app.UseWebSockets();

// Liveness probe used by the self-update helper (deploy.sh curls this on localhost). No secrets.
app.MapGet("/health", () => Results.Text("ok"));

// First-run helper: "RemoteServer mint-blob" verifies prerequisites and prints the first bootstrap blob,
// then exits without starting the web server. Solves the chicken-and-egg of the very first enrollment
// (the console needs an enrolled agent's tunnel to reach /admin, but there is no agent yet).
if (args.Contains("mint-blob"))
{
    Environment.ExitCode = await RunMintBlobAsync(app);
    return;
}

// === Session auth for /admin: requires a valid Bearer token. Transport is provided by the device SSH tunnel.
// /auth/* endpoints are public through the tunnel and validate themselves. Until user setup
// (password change / TOTP enrollment) is complete, console endpoints return 403. ===
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/admin")) { await next(); return; }

    // Branding (owner + support) is public through the tunnel and needed before sign-in.
    // nginx exposes /admin/ only from localhost via tunnel, so it is not reachable externally.
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

    // Role gate: operator can only see device/group lists, start open-tunnel,
    // and poll the result for tunnel requests they created.
    // Endpoints do filtering/grant checks; every other /admin endpoint is admin-only.
    var user = v.Value.User;
    if (!AuthService.IsAdmin(user))
    {
        var m = ctx.Request.Method;
        var p = ctx.Request.Path.Value ?? "";
        bool operatorAllowed =
            (m == "GET" && p == "/admin/devices")
            || (m == "GET" && p.StartsWith("/admin/devices/access-result/", StringComparison.Ordinal))
            || (m == "POST" && p.StartsWith("/admin/devices/", StringComparison.Ordinal) && p.EndsWith("/open-tunnel", StringComparison.Ordinal))
            // Operators set their own viewer scale; the pref roams with their account.
            || (m == "PUT" && p == "/admin/me/viewer-prefs");
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

// === Sign-in / 2FA, reachable through the device SSH tunnel; endpoints validate themselves. ===
app.MapPost("/auth/login", async (HttpContext ctx, AppDbContext db, AuthService auth, SecretProtector protector, IEmailSender email, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    LoginRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.LoginRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest();

    // Minimum-version gate: outdated clients get mandatory update info instead of a session.
    if (await ClientUpdateGateAsync(req.ClientVersion, req.Channel, opt.Value.MinClientVersion, db, ct) is { } gate)
        return Results.Json(gate, AgentJsonContext.Default.LoginResponse);

    // Device-level login lockout: when locked, no sign-in is allowed until an admin unlocks it.
    var device = await FindDeviceAsync(db, req.DeviceId, ct);
    if (device?.LoginLockedAt is not null)
        return Results.Json(new AuthError { Error = "device_locked" }, AgentJsonContext.Default.AuthError, statusCode: 403);

    var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
        .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
    {
        await RegisterLoginFailAsync(db, email, ctx, device, req.Username, "login-failed",
            user is null ? "unknown_user" : "bad_password", ct);
        return Results.Json(new AuthError { Error = "invalid_credentials" }, AgentJsonContext.Default.AuthError, statusCode: 401);
    }

    // If TOTP is configured, the code is mandatory — unless this device is remembered (trusted).
    // The password was already verified above; trust only skips the second factor.
    bool deviceTrusted = await auth.IsDeviceTrustedAsync(req.TrustToken, user.Id, ct);
    if (user.TotpConfirmed && !deviceTrusted)
    {
        var secret = protector.TryUnprotect(user.TotpSecret);
        if (secret is null || !TotpService.Verify(secret, req.Totp ?? ""))
        {
            // Invalid TOTP counts as a failed attempt, but totp_required (no code submitted yet) does not.
            if (!string.IsNullOrWhiteSpace(req.Totp))
                await RegisterLoginFailAsync(db, email, ctx, device, req.Username, "login-failed", "bad_totp", ct);
            return Results.Json(new AuthError { Error = string.IsNullOrWhiteSpace(req.Totp) ? "totp_required" : "totp_invalid" },
                AgentJsonContext.Default.AuthError, statusCode: 401);
        }
    }

    await ResetLoginFailAsync(db, device, ct); // successful sign-in resets the counter
    var token = await auth.CreateSessionAsync(user, ct);
    user.LastLoginAt = DateTimeOffset.UtcNow;

    var resp = new LoginResponse
    {
        Token = token,
        Role = AuthService.RoleOf(user),
        MustChangePassword = user.MustChangePassword,
        TotpEnrollRequired = !user.TotpConfirmed,
        ViewerScale = user.ViewerScale,
        ViewerColor = user.ViewerColor,
    };

    // "Remember this device": issue a trust token after a real 2FA login (not when the device was already
    // trusted, so a stolen trust cannot perpetually renew itself). The password is still required next time.
    if (user.TotpConfirmed && !deviceTrusted && req.RememberDevice)
        resp.TrustToken = await auth.IssueDeviceTrustAsync(user.Id, device?.Hostname, ct);

    // First sign-in or no TOTP yet: generate an enrollment secret, store encrypted and unconfirmed.
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

// === Windows Hello (passkey-style) ===
// Sign-in 1/2: challenge. Always return one so user/Hello existence is not revealed.
app.MapPost("/auth/hello/challenge", async (HttpContext ctx, HelloChallengeStore challenges, CancellationToken ct) =>
{
    HelloChallengeRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.HelloChallengeRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest();
    var nonce = challenges.Issue(req.Username.Trim());
    return Results.Json(new HelloChallengeResponse { Challenge = Convert.ToBase64String(nonce) }, AgentJsonContext.Default.HelloChallengeResponse);
});

// Sign-in 2/2: verify signed challenge with stored public key, then create a session.
app.MapPost("/auth/hello/login", async (HttpContext ctx, AppDbContext db, AuthService auth, HelloChallengeStore challenges, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    HelloLoginRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.HelloLoginRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Signature))
        return Results.Json(new AuthError { Error = "invalid_request" }, AgentJsonContext.Default.AuthError, statusCode: 400);

    // Minimum-version gate, same as /auth/login: outdated clients receive a mandatory update.
    if (await ClientUpdateGateAsync(req.ClientVersion, req.Channel, opt.Value.MinClientVersion, db, ct) is { } gate)
        return Results.Json(gate, AgentJsonContext.Default.LoginResponse);

    // Device-level lockout applies to Hello too, otherwise it would bypass the password lock.
    var device = await FindDeviceAsync(db, req.DeviceId, ct);
    if (device?.LoginLockedAt is not null)
        return Results.Json(new AuthError { Error = "device_locked" }, AgentJsonContext.Default.AuthError, statusCode: 403);

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
    await ResetLoginFailAsync(db, device, ct); // successful Hello sign-in resets the counter
    var token = await auth.CreateSessionAsync(user, ct);
    user.LastLoginAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    return Results.Json(new LoginResponse
    {
        Token = token,
        Role = AuthService.RoleOf(user),
        MustChangePassword = user.MustChangePassword,
        TotpEnrollRequired = !user.TotpConfirmed,
        ViewerScale = user.ViewerScale,
        ViewerColor = user.ViewerColor,
    }, AgentJsonContext.Default.LoginResponse);
});

// Register a Hello device while signed in. Any user can register their own, so validate here instead of /admin gate.
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
        DeviceName = string.IsNullOrWhiteSpace(req.DeviceName) ? L.Program_UnknownDevice : req.DeviceName.Trim(),
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

// Setup steps. Mid-setup users can call these, so validation is here instead of /admin gate.
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

// === Password recovery, public through the tunnel before sign-in. ===
// Code request: response is always OK for anti-enumeration. A code is sent only when
// username + email match an active user.
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
    if (device?.LoginLockedAt is not null) return Results.NoContent(); // locked device; silently do not send

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == uname && u.IsActive, ct);
    if (user is not null && string.Equals(user.Email, mail, StringComparison.OrdinalIgnoreCase))
    {
        var code = SetResetCode(user);
        await db.SaveChangesAsync(ct);
        await EmailResetCodeAsync(email, user, code, req.Language, ct); // in the requesting client's language
        await AuditAsync(db, ctx, "password-code-requested", null, null, actorOverride: user.Username);
    }
    else
    {
        // Mismatch (wrong email or no such user): logged failed attempt plus counter.
        await RegisterLoginFailAsync(db, email, ctx, device, uname, "password-code-failed",
            user is null ? "unknown_user" : "email_mismatch", ct);
    }
    // Response is always OK for anti-enumeration.
    return Results.NoContent();
});

// Set a new password with the received code.
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
            user is null ? "unknown_user" : "bad_token", ct);
        return Results.Json(new AuthError { Error = "invalid_code" }, AgentJsonContext.Default.AuthError, statusCode: 400);
    }

    user.PasswordHash = PasswordHasher.Hash(req.NewPassword!);
    user.MustChangePassword = false;          // user set it themselves
    user.PasswordChangedAt = DateTimeOffset.UtcNow;
    user.ResetCodeHash = null; user.ResetCodeExpiresAt = null;
    await auth.RevokeAllForUserAsync(user.Id, ct);
    await db.SaveChangesAsync(ct);
    await ResetLoginFailAsync(db, device, ct); // successful self-reset resets the counter
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
        ViewerScale = u.ViewerScale,
        ViewerColor = u.ViewerColor,
    }, AgentJsonContext.Default.MeResponse);
});

// === Agent WSS command channel ===
// Agents keep their outbound connection here; the server pushes signed commands over it.
// In production, nginx terminates TLS and validates the client certificate (mTLS).
// Device ID comes from the certificate CN. Without a cert, ?deviceId= is the dev fallback.
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
    log.LogInformation(L.Program_AgentConnectedDevice, deviceId);

    // On connect, deliver pending Queued commands in a short scope so DbContext is not
    // held for the entire connection lifetime.
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
    catch (OperationCanceledException) { /* shutdown/disconnect */ }
    catch (WebSocketException ex) { log.LogDebug(ex, L.Program_WSClosedDevice, deviceId); }
    finally
    {
        registry.Unregister(deviceId, socket);
        log.LogInformation(L.Program_AgentDisconnectedDevice, deviceId);
    }
});

// === Telemetry ingest. Production uses mTLS behind nginx. ===
app.MapPost("/api/telemetry", async (HttpContext ctx, ITelemetrySink sink, AppDbContext db) =>
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

    // Steer the agent's bastion transport via the telemetry response (mTLS-authenticated, non-secret;
    // the bastion host key stays pinned regardless of port). Old agents ignore the body; new ones apply it.
    var transport = await db.Devices.Where(d => d.DeviceId == deviceId)
        .Select(d => d.BastionTransport).FirstOrDefaultAsync(ctx.RequestAborted);
    return Results.Json(new AgentConfigResponse { BastionTransport = transport ?? "auto" },
        AgentJsonContext.Default.AgentConfigResponse);
});

// === Device reports its VNC password over mTLS into devices.vnc_secret. ===
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

    device.VncSecret = protector.Protect(report.Secret); // encryption at rest
    device.VncSecretUpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ctx.RequestAborted);
    return Results.NoContent();
});

// === Admin. localhost-only through nginx; the client reaches it over SSH tunnel. ===

// Device list from DB, online state from registry, plus VNC password for client autoconnect.
app.MapGet("/admin/devices", async (HttpContext ctx, AppDbContext db, AgentConnectionRegistry registry, SecretProtector protector, AuthService auth, CancellationToken ct) =>
{
    var devices = await db.Devices.Include(d => d.Group).OrderBy(d => d.Hostname).ToListAsync(ct);

    // Operators see only granted devices; admins see everything.
    var me = (User)ctx.Items["user"]!;
    if (!AuthService.IsAdmin(me))
    {
        var (gids, dids) = await auth.GrantsAsync(me.Id, ct);
        devices = devices.Where(d => AuthService.CanAccessDevice(d, gids, dids)).ToList();
    }

    // In-flight update commands (Queued/Sent/Acked) per device, for the rollout/pending indicator.
    // Cleared once the device reports the target version (handles agents that restart before acking).
    var byId = devices.ToDictionary(d => d.Id);
    var deviceGuids = byId.Keys.ToList();
    var inflight = await db.Commands
        .Where(c => c.Type == CommandTypes.Update
            && (c.Status == CommandStatus.Queued || c.Status == CommandStatus.Sent || c.Status == CommandStatus.Acked)
            && deviceGuids.Contains(c.DeviceId))
        .ToListAsync(ct);
    var pendingInfo = new Dictionary<Guid, string>();
    foreach (var grp in inflight.GroupBy(c => c.DeviceId))
    {
        if (!byId.TryGetValue(grp.Key, out var dev)) continue;
        var cmd = grp.OrderByDescending(c => c.CreatedAt).First();
        var cd = cmd.PayloadJson is null ? null : JsonSerializer.Deserialize(cmd.PayloadJson, AgentJsonContext.Default.CommandData);
        var target = string.IsNullOrWhiteSpace(cd?.UpdateTarget) ? "agent" : cd!.UpdateTarget!;
        var ver = cd?.UpdateVersion ?? "";
        var reported = target switch { "updater" => dev.HelperVersion, "vnc" => dev.VncVersion, "client" => dev.ClientVersion, _ => dev.AgentVersion };
        if (string.IsNullOrEmpty(ver) || reported is null || !reported.StartsWith(ver, StringComparison.OrdinalIgnoreCase))
            pendingInfo[grp.Key] = $"{target} {ver} · {cmd.Status}";
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
        BastionTransport = d.BastionTransport,
        UnattendedAllowed = d.UnattendedAllowed,
        ConsentRequired = d.ConsentRequired,
        AgentVersion = d.AgentVersion,
        HelperVersion = d.HelperVersion,
        VncVersion = d.VncVersion,
        ClientVersion = d.ClientVersion,
        OsVersion = d.OsVersion,
        Manufacturer = d.Manufacturer,
        Model = d.Model,
        SerialNumber = d.SerialNumber,
        AgentRestarts = d.AgentRestarts,
        LastIncident = d.LastIncident,
        VncLocked = d.VncLocked,
        BootTimeUtc = d.BootTimeUtc,
        IpAddress = d.IpAddress,
        PublicIpAddress = d.PublicIpAddress,
        PublicIpReverse = d.PublicIpReverse,
        WifiSsid = d.WifiSsid,
        VpnActive = d.VpnActive,
        LoggedInUser = d.LoggedInUser,
        LoginFailCount = d.LoginFailCount,
        LoginLocked = d.LoginLockedAt is not null,
        Note = protector.TryUnprotect(d.Note),
        UpdatePending = pendingInfo.ContainsKey(d.Id),
        UpdatePendingInfo = pendingInfo.GetValueOrDefault(d.Id),
    }).ToList();
    return Results.Json(list, AgentJsonContext.Default.ListDeviceInfo);
});

// Updates device admin fields (group, flags, note). Null fields are unchanged.
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
    if (upd.BastionTransport is not null && (upd.BastionTransport is "auto" or "ssl443" or "ssh22" or "wss443")) device.BastionTransport = upd.BastionTransport;
    if (upd.UnattendedAllowed is not null) device.UnattendedAllowed = upd.UnattendedAllowed;
    if (upd.ConsentRequired is not null) device.ConsentRequired = upd.ConsentRequired;
    if (upd.Note is not null) device.Note = upd.Note.Length == 0 ? null : protector.Protect(upd.Note);
    if (upd.Status is not null && Enum.TryParse<DeviceStatus>(upd.Status, ignoreCase: true, out var st)) device.Status = st;

    await db.SaveChangesAsync(ctx.RequestAborted);
    await AuditAsync(db, ctx, "device-update", device.Id, device.Hostname);
    return Results.NoContent();
});

// Delete a device and its dependent rows (telemetry, commands, sessions). Audit history is kept.
// The agent keeps its local enrollment, so to fully re-provision a device, re-enroll it afterwards.
app.MapDelete("/admin/devices/{deviceId}", async (string deviceId, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    var gid = device.Id;
    var host = device.Hostname;

    // No FK cascade: these store DeviceId as a plain Guid, so remove them explicitly.
    await db.DeviceTelemetry.Where(t => t.DeviceId == gid).ExecuteDeleteAsync(ct);
    await db.Set<RemoteServer.Data.Entities.Command>().Where(c => c.DeviceId == gid).ExecuteDeleteAsync(ct);
    await db.Set<RemoteServer.Data.Entities.RemoteSession>().Where(s => s.DeviceId == gid).ExecuteDeleteAsync(ct);
    db.Devices.Remove(device);
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "device-delete", null, host);
    return Results.NoContent();
});

// List and create groups.
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
    // Devices in the group become ungrouped; they are not deleted.
    var devices = await db.Devices.Where(d => d.GroupId == id).ToListAsync(ct);
    foreach (var d in devices) d.GroupId = null;
    db.DeviceGroups.Remove(group);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapGet("/admin/devices/online", (AgentConnectionRegistry registry) => Results.Ok(registry.ConnectedDevices));

// Update command, only when updates are allowed on the device (UpdateAllowed).
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

// Upload an update package to one channel: body is raw exe; query has channel (rtm/beta),
// component (agent/updater), and version. Server stores it, computes SHA-256, and inserts
// a ReleasePackage row; from then on it is the current package for (channel, component).
app.MapPost("/admin/packages", async (HttpContext ctx, AppDbContext db, IOptions<ServerOptions> opt) =>
{
    var channel = Norm(ctx.Request.Query["channel"], "rtm");
    var component = Norm(ctx.Request.Query["component"], "agent");
    var version = ctx.Request.Query["version"].ToString();
    if (string.IsNullOrWhiteSpace(version)) return Results.BadRequest(new { error = "version_required" });
    if (channel is not ("rtm" or "beta")) return Results.BadRequest(new { error = "bad_channel" });
    if (component is not ("agent" or "updater" or "client" or "vnc")) return Results.BadRequest(new { error = "bad_component" });

    // Internal collision-free file name: {component}-{channel}-{version}.{ext}. vnc arrives as MSI.
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

// === Server self-update (admin) ===
// The client uploads a GitHub-built server tarball (and optional schema upgrade.sql), then triggers
// an update. A privileged systemd helper (remoteserver-update.path -> .service running deploy.sh as
// root, in its own cgroup so it survives the server stop) does backup/stop/migrate/swap/start/
// health-check/auto-rollback. The server only stages files in its own writable UpdatesDir and drops a
// trigger file; it never needs sudo. These routes sit under /admin, so the session-auth gate applies.

app.MapPost("/admin/server/package", async (HttpContext ctx, AppDbContext db, IOptions<ServerOptions> opt) =>
{
    var kind = ctx.Request.Query["kind"].ToString().Trim().ToLowerInvariant();
    if (kind is not ("tar" or "sql")) return Results.BadRequest(new { error = "bad_kind" });
    var incoming = Path.Combine(opt.Value.UpdatesDir, "incoming");
    Directory.CreateDirectory(incoming);
    // A new build resets staging so a later binary-only update cannot reuse a stale upgrade.sql.
    if (kind == "tar")
    {
        var oldSql = Path.Combine(incoming, "upgrade.sql");
        if (File.Exists(oldSql)) File.Delete(oldSql);
    }
    var path = Path.Combine(incoming, kind == "tar" ? "server.tar.gz" : "upgrade.sql");
    await using (var fs = File.Create(path))
        await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
    var size = new FileInfo(path).Length;
    await AuditAsync(db, ctx, "server-package", null, $"{kind} · {size} bytes");
    return Results.Ok(new { kind, size });
});

app.MapPost("/admin/server/update", async (HttpContext ctx, AppDbContext db, IOptions<ServerOptions> opt) =>
{
    var dir = opt.Value.UpdatesDir;
    if (!File.Exists(Path.Combine(dir, "incoming", "server.tar.gz"))) return Results.BadRequest(new { error = "no_package" });
    bool sql = File.Exists(Path.Combine(dir, "incoming", "upgrade.sql"));
    foreach (var f in new[] { "result.status", "result.log", "result.at" })
    { var p = Path.Combine(dir, f); if (File.Exists(p)) File.Delete(p); }
    // systemd path unit watches apply.trigger and fires the root deploy service.
    await File.WriteAllTextAsync(Path.Combine(dir, "apply.trigger"), DateTimeOffset.UtcNow.ToString("o"), ctx.RequestAborted);
    await AuditAsync(db, ctx, "server-update", null, sql ? "tar + sql" : "tar");
    return Results.StatusCode(202);
});

app.MapPost("/admin/server/rollback", async (HttpContext ctx, AppDbContext db, IOptions<ServerOptions> opt) =>
{
    var dir = opt.Value.UpdatesDir;
    if (!File.Exists(Path.Combine(dir, "last_backup"))) return Results.BadRequest(new { error = "no_backup" });
    foreach (var f in new[] { "result.status", "result.log", "result.at" })
    { var p = Path.Combine(dir, f); if (File.Exists(p)) File.Delete(p); }
    await File.WriteAllTextAsync(Path.Combine(dir, "rollback.trigger"), DateTimeOffset.UtcNow.ToString("o"), ctx.RequestAborted);
    await AuditAsync(db, ctx, "server-rollback", null, "rollback");
    return Results.StatusCode(202);
});

app.MapGet("/admin/server/status", (IOptions<ServerOptions> opt) =>
{
    var dir = opt.Value.UpdatesDir;
    var incoming = Path.Combine(dir, "incoming");
    var tar = Path.Combine(incoming, "server.tar.gz");
    string? Read(string n) { var p = Path.Combine(dir, n); return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
    ServerUpdateResult? result = null;
    var st = Read("result.status");
    if (st is not null)
        result = new ServerUpdateResult { Ok = st == "ok", Message = Read("result.log") ?? "", At = Read("result.at") ?? "" };
    var info = new ServerUpdateStatus
    {
        Version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?",
        StagedTar = File.Exists(tar),
        StagedTarSize = File.Exists(tar) ? new FileInfo(tar).Length : 0,
        StagedSql = File.Exists(Path.Combine(incoming, "upgrade.sql")),
        LastResult = result,
        BackupAvailable = File.Exists(Path.Combine(dir, "last_backup")),
        HelperReady = File.Exists("/opt/remoteserver-update/deploy.sh"),
    };
    return Results.Json(info, AgentJsonContext.Default.ServerUpdateStatus);
});

// Current packages per channel and component, used by the client channel view.
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

// Rollout: sends a channel's current package to every approved and updatable device on it.
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
        // Already on target version? Loose match: reported "2.0.0.0" starts with "2.0.0".
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
    await AuditAsync(db, ctx, "rollout", null, L.Format(L.Program_Devices, comp, channel, pkg.Version, sent));
    return Results.Ok(new { channel, component = comp, version = pkg.Version, devices = devices.Count, sent, skipped });
});

// Promotion: makes a channel's current package current on the target channel, using the same file.
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

// Updates one device to the current package on its own channel, channel-aware.
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

// Serves update packages behind mTLS under /api, for enrolled agents only.
app.MapGet("/api/updates/{fileName}", (string fileName, IOptions<ServerOptions> opt) =>
{
    var safe = Path.GetFileName(fileName);
    var path = Path.Combine(opt.Value.PackagesDir, safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream", safe) : Results.NotFound();
});

// Open tunnel: uses the device's stable port assigned at enrollment; can be overridden by query.
app.MapPost("/admin/devices/{deviceId}/open-tunnel", async (
    string deviceId, int? remotePort, HttpContext ctx, AppDbContext db, CommandService commands, AuthService auth, AccessResultStore accessResults, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();

    // Operators can open tunnels only to granted devices.
    var me = (User)ctx.Items["user"]!;
    if (!AuthService.IsAdmin(me))
    {
        var (gids, dids) = await auth.GrantsAsync(me.Id, ct);
        if (!AuthService.CanAccessDevice(device, gids, dids))
            return Results.Json(new AuthError { Error = "forbidden" }, AgentJsonContext.Default.AuthError, statusCode: 403);
    }

    var port = remotePort is > 0 ? remotePort.Value
             : device.TunnelPort ?? Random.Shared.Next(50000, 60000); // fallback for old devices without port

    // Effective access policy: device override, then group, then defaults (no consent / unattended allowed).
    // The agent uses this to prompt/decide before opening the tunnel.
    var grp = device.GroupId is { } gid ? await db.DeviceGroups.FirstOrDefaultAsync(x => x.Id == gid, ct) : null;
    bool consentRequired = device.ConsentRequired ?? grp?.ConsentRequired ?? false;
    bool unattendedAllowed = device.UnattendedAllowed ?? grp?.UnattendedAllowed ?? true;

    var cmd = await commands.EnqueueAsync(
        deviceId, CommandTypes.OpenTunnel,
        new CommandData { RemotePort = port, ConsentRequired = consentRequired, UnattendedAllowed = unattendedAllowed },
        createdBy: null, ct);
    if (cmd is null) return Results.NotFound();

    // Bind context to nonce: who requested access to which device, so the agent outcome can be audited.
    accessResults.SetPending(cmd.Nonce ?? "", me.Username, device.Id, device.Hostname);

    return Results.Json(
        new OpenTunnelResult { DeviceId = deviceId, RemotePort = port, Status = cmd.Status.ToString(), Nonce = cmd.Nonce ?? "" },
        AgentJsonContext.Default.OpenTunnelResult);
});

// Access request result, polled by console by nonce after opening a tunnel.
// Empty outcome means no answer yet; agent is working or asking the user.
app.MapGet("/admin/devices/access-result/{nonce}", (string nonce, HttpContext ctx, AccessResultStore accessResults) =>
{
    var me = (User)ctx.Items["user"]!;
    var entry = accessResults.GetEntry(nonce);
    if (entry is not null && !AuthService.IsAdmin(me) && !string.Equals(entry.Actor, me.Username, StringComparison.Ordinal))
        return Results.Json(new AuthError { Error = "forbidden" }, AgentJsonContext.Default.AuthError, statusCode: 403);

    return Results.Json(new AccessResultInfo { Outcome = entry?.Outcome ?? "" }, AgentJsonContext.Default.AccessResultInfo);
});

// Messages tab: "is your machine free now?" — Yes/No WTS prompt on the device (30s). The outcome
// (available / busy / no-user) comes back via the access-result uplink; the console polls the nonce.
app.MapPost("/admin/devices/{deviceId}/ask-availability", async (
    string deviceId, HttpContext ctx, AppDbContext db, CommandService commands, AccessResultStore accessResults, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    var me = (User)ctx.Items["user"]!;
    var from = string.IsNullOrWhiteSpace(me.Name) ? me.Username : me.Name!;

    var cmd = await commands.EnqueueAsync(deviceId, CommandTypes.Message,
        new CommandData { MessageKind = "availability", MessageFrom = from }, createdBy: null, ct);
    if (cmd is null) return Results.NotFound();
    accessResults.SetPending(cmd.Nonce ?? "", me.Username, device.Id, device.Hostname);
    return Results.Json(new OpenTunnelResult { DeviceId = deviceId, Status = cmd.Status.ToString(), Nonce = cmd.Nonce ?? "" }, AgentJsonContext.Default.OpenTunnelResult);
});

// Messages tab: send a plain message ("{operator} sent a message") shown with OK on the device.
app.MapPost("/admin/devices/{deviceId}/send-message", async (
    string deviceId, string? text, HttpContext ctx, AppDbContext db, CommandService commands, AccessResultStore accessResults, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(text)) return Results.BadRequest(new { error = "no_text" });
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    var me = (User)ctx.Items["user"]!;
    var from = string.IsNullOrWhiteSpace(me.Name) ? me.Username : me.Name!;

    var cmd = await commands.EnqueueAsync(deviceId, CommandTypes.Message,
        new CommandData { MessageKind = "text", MessageFrom = from, MessageText = text.Trim() }, createdBy: null, ct);
    if (cmd is null) return Results.NotFound();
    accessResults.SetPending(cmd.Nonce ?? "", me.Username, device.Id, device.Hostname);
    await AuditAsync(db, ctx, "device-message", device.Id, $"{from}: {text.Trim()}");
    return Results.Json(new OpenTunnelResult { DeviceId = deviceId, Status = cmd.Status.ToString(), Nonce = cmd.Nonce ?? "" }, AgentJsonContext.Default.OpenTunnelResult);
});

// Commands tab: a fixed power action on the device (restart / force-restart / cancel / logout). The agent
// maps the keyword to a vetted action — no shell string crosses the wire. The outcome
// (scheduled / cancelled / logged-out / no-user / failed) comes back via access-result; the console polls.
app.MapPost("/admin/devices/{deviceId}/power", async (
    string deviceId, string? action, HttpContext ctx, AppDbContext db, CommandService commands, AccessResultStore accessResults, CancellationToken ct) =>
{
    var act = (action ?? "").Trim().ToLowerInvariant();
    if (act is not ("restart" or "force-restart" or "cancel" or "logout"))
        return Results.BadRequest(new { error = "bad_action" });
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();
    var me = (User)ctx.Items["user"]!;

    var cmd = await commands.EnqueueAsync(deviceId, CommandTypes.Power,
        new CommandData { PowerAction = act }, createdBy: null, ct);
    if (cmd is null) return Results.NotFound();
    accessResults.SetPending(cmd.Nonce ?? "", me.Username, device.Id, device.Hostname);
    await AuditAsync(db, ctx, "device-power", device.Id, act);
    return Results.Json(new OpenTunnelResult { DeviceId = deviceId, Status = cmd.Status.ToString(), Nonce = cmd.Nonce ?? "" }, AgentJsonContext.Default.OpenTunnelResult);
});

// Audit log query with filters: action key, actor username, deviceId, limit.
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

// === Enrollment ===
// Device sends CSR + token. On success, signed cert + CA are returned.
// On failure, machine-readable code is returned and localized by the client.
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

// === Token generation, currently admin-only and protected by auth+2FA. ===
app.MapPost("/admin/tokens", async (EnrollmentService enroll, int? maxUses, int? expiresInHours, CancellationToken ct) =>
{
    var (raw, _) = await enroll.CreateTokenAsync(maxUses ?? 1, expiresInHours, groupId: null, note: null, ct);
    return Results.Ok(new { token = raw, maxUses = maxUses ?? 1, expiresInHours });
});

// === Bootstrap blob for tokenless self-install: site token + server URL in one string. ===
// Generated token has AutoApprove=false, so devices enrolled with it become Pending.
app.MapPost("/admin/bootstrap", async (
    HttpContext ctx, AppDbContext db, EnrollmentService enroll, IOptions<ServerOptions> opt,
    string? serverUrl, Guid? groupId, int? maxUses, int? expiresInHours, CancellationToken ct) =>
{
    var url = !string.IsNullOrWhiteSpace(serverUrl) ? serverUrl : opt.Value.PublicUrl;
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "no_server_url", hint = L.Program_SetServerPublicUrlOrProvide });

    var (raw, _) = await enroll.CreateTokenAsync(maxUses ?? 100000, expiresInHours, groupId, note: "bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = raw });
    await AuditAsync(db, ctx, "bootstrap-create", null, $"max {(maxUses ?? 100000)}");
    return Results.Ok(new { blob, url = url.TrimEnd('/'), token = raw, groupId, maxUses = maxUses ?? 100000, expiresInHours });
});

// === Bootstrap/enrollment token (blob) list, revoke, and delete. ===
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

// Edit blob/token: max installs and/or expiry. Disable is revoke, handled separately,
// so MaxUses cannot go below already used count (UseCount).
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

// === Server settings: branding (owner + support) and email sending. ===
app.MapGet("/admin/settings", async (AppDbContext db, CancellationToken ct) =>
{
    var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
    var info = new ServerSettingsInfo();
    if (s is not null)
    {
        info.OwnerName = s.OwnerName; info.SupportPhone = s.SupportPhone; info.SupportEmail = s.SupportEmail;
        info.Language = string.IsNullOrWhiteSpace(s.Language) ? "auto" : s.Language;
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
    s.Language = string.IsNullOrWhiteSpace(upd.Language) ? "auto" : upd.Language.Trim().ToLowerInvariant();
    s.EmailProvider = string.IsNullOrWhiteSpace(upd.EmailProvider) ? "none" : upd.EmailProvider.Trim().ToLowerInvariant();
    s.SmtpHost = Nz(upd.SmtpHost); s.SmtpPort = upd.SmtpPort <= 0 ? 587 : upd.SmtpPort; s.SmtpUseTls = upd.SmtpUseTls;
    s.SmtpUser = Nz(upd.SmtpUser); s.SmtpFrom = Nz(upd.SmtpFrom);
    if (!string.IsNullOrEmpty(upd.SmtpPassword)) s.SmtpPasswordEnc = protector.Protect(upd.SmtpPassword);
    s.GraphTenantId = Nz(upd.GraphTenantId); s.GraphClientId = Nz(upd.GraphClientId); s.GraphSender = Nz(upd.GraphSender);
    if (!string.IsNullOrEmpty(upd.GraphClientSecret)) s.GraphClientSecretEnc = protector.Protect(upd.GraphClientSecret);

    // Secret expiry: max 2 years from now. If expiry changed, warnings can fire again.
    var prevExpiry = s.GraphSecretExpiresAt;
    if (upd.GraphSecretExpiresAt is { } exp)
    {
        var max = DateTimeOffset.UtcNow.AddYears(2);
        s.GraphSecretExpiresAt = exp > max ? max : exp;
    }
    else s.GraphSecretExpiresAt = null;
    if (s.GraphSecretExpiresAt != prevExpiry) s.SecretExpiryNotifiedAt = null;

    await db.SaveChangesAsync(ct);

    // Audit detail: snapshot saved non-secret values; for secrets, only the fact is recorded, never the value.
    var detail = $"provider={s.EmailProvider}; owner={Q(s.OwnerName)}; phone={Q(s.SupportPhone)}; email={Q(s.SupportEmail)}";
    if (s.EmailProvider == "smtp")
        detail += $"; smtp={Q(s.SmtpHost)}:{s.SmtpPort} tls={s.SmtpUseTls} user={Q(s.SmtpUser)} from={Q(s.SmtpFrom)}";
    if (s.EmailProvider == "graph")
        detail += $"; graph tenant={Q(s.GraphTenantId)} client={Q(s.GraphClientId)} sender={Q(s.GraphSender)} expires={s.GraphSecretExpiresAt:yyyy-MM-dd}";
    if (!string.IsNullOrEmpty(upd.SmtpPassword)) detail += L.Program_SmtpPasswordChanged;
    if (!string.IsNullOrEmpty(upd.GraphClientSecret)) detail += L.Program_GraphSecretChanged;

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
        L.Program_ThisIsATestEmail, ct);
    await AuditAsync(db, ctx, "settings-test-email", null, ok ? req.To.Trim() : L.Format(L.Program_Error, err));
    return ok ? Results.Ok(new { ok = true }) : Results.Problem(err ?? "send_failed");
});

// Per-operator viewer preference (currently only the TightVNC scale: "auto" or 1..400 percent).
// Stored on the account so it roams to any console the operator signs in from. The /admin gate already
// validated the session; operators are allowed here (see the operator role gate above).
app.MapPut("/admin/me/viewer-prefs", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var me = (User)ctx.Items["user"]!;
    var req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.ViewerPrefsRequest, ct);
    var scale = NormalizeViewerScale(req?.Scale);
    if (scale is null) return Results.BadRequest(new AuthError { Error = "invalid_scale" });
    var color = NormalizeViewerColor(req?.Color);

    var u = await db.Users.FirstAsync(x => x.Id == me.Id, ct);
    u.ViewerScale = scale;
    u.ViewerColor = color;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// Public branding through the tunnel before sign-in; auth gate has an exception for it.
app.MapGet("/admin/branding", async (AppDbContext db, CancellationToken ct) =>
{
    var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
    var b = new BrandingInfo { OwnerName = s?.OwnerName, SupportPhone = s?.SupportPhone, SupportEmail = s?.SupportEmail };
    return Results.Json(b, AgentJsonContext.Default.BrandingInfo);
});

// === MSI build for a group from the current executables on one channel (wixl). ===
// Places agent + updater executables plus group bootstrap.dat, then installs via install-service.
app.MapPost("/admin/msi", async (
    string? group, string? channel, bool? client, bool? shortcut, HttpContext ctx, AppDbContext db, EnrollmentService enroll,
    MsiBuilder msi, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    var ch = string.IsNullOrWhiteSpace(channel) ? "rtm" : channel.Trim().ToLowerInvariant();
    bool includeClient = client ?? true;       // install console client by default
    bool startMenuShortcut = shortcut ?? true; // include Start menu shortcut by default

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

    // Optional console client: current 'client' package on the channel when requested and available.
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

    // Bundle the current TightVNC (vnc) MSI so the agent installs it on first run — no separate rollout needed.
    string? vncMsi = null;
    var vncPkg = await db.ReleasePackages.Where(p => p.Channel == ch && p.Component == "vnc")
        .OrderByDescending(p => p.UploadedAt).FirstOrDefaultAsync(ct);
    if (vncPkg is not null)
    {
        var vp = Path.Combine(opt.Value.PackagesDir, vncPkg.FileName);
        if (File.Exists(vp)) vncMsi = vp;
    }

    // Group bootstrap: AutoApprove=false site token makes installed devices Pending.
    var (rawToken, tokenEntity) = await enroll.CreateTokenAsync(100000, expiresInHours: null, groupId, note: "msi-bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = rawToken });

    var ownerName = (await db.ServerSettings.FirstOrDefaultAsync(ct))?.OwnerName;
    var res = await msi.BuildAsync(agentExe, updaterExe, clientExe, blob, agentPkg.Version, label, startMenuShortcut, ownerName, vncMsi, ct);
    if (!res.Ok) return Results.Problem(res.Error ?? "msi_failed");

    // Bind the blob to the generated MSI so the bootstrap view can show which MSI owns it.
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

// MSI download, admin/localhost, reached by the client over SSH tunnel.
app.MapGet("/admin/msi/{fileName}", (string fileName, IOptions<ServerOptions> opt) =>
{
    var safe = Path.GetFileName(fileName);
    var path = Path.Combine(opt.Value.PackagesDir, safe);
    return File.Exists(path) ? Results.File(path, "application/x-msi", safe) : Results.NotFound();
});

// === User management, admin-only; middleware already excluded operators. ===
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
    var code = SetResetCode(user);   // always issue a recovery token; admin UI displays it
    db.Users.Add(user);
    await db.SaveChangesAsync(ct);
    await SetRoleAsync(db, user.Id, role, ct);
    await db.SaveChangesAsync(ct);

    string? serverLang = req.EmailCode ? ResolveServerLanguage(await db.ServerSettings.FirstOrDefaultAsync(ct)) : null;
    bool emailSent = req.EmailCode && await EmailResetCodeAsync(email, user, code, serverLang, ct); // admin-triggered → server language

    await AuditAsync(db, ctx, "user-create", null, $"{user.Username} · {role}{(emailSent ? " · token emailed" : "")}");
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

    // Self-lockout protection: caller admin cannot deactivate or demote themselves.
    var me = (User)ctx.Items["user"]!;
    if (id == me.Id && upd.IsActive == false) return Results.BadRequest(new { error = "self_deactivate" });
    if (id == me.Id && upd.Role == "operator") return Results.BadRequest(new { error = "self_demote" });

    if (upd.Role is "admin" or "operator") await SetRoleAsync(db, id, upd.Role, ct);
    if (upd.Name is not null) user.Name = upd.Name.Trim().Length == 0 ? null : upd.Name.Trim();
    if (upd.Email is not null) user.Email = upd.Email.Trim().Length == 0 ? null : upd.Email.Trim();
    if (upd.IsActive is { } act)
    {
        user.IsActive = act;
        if (!act) await auth.RevokeAllForUserAsync(id, ct); // deactivation signs the user out immediately
    }
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "user-update", null, user.Username);
    return Results.NoContent();
});

// Delete a user (admin). Self-delete is blocked, which also guarantees at least one admin remains
// (you can only delete another admin, so the deleting admin survives). Sessions/grants/roles/Hello/
// trusts are removed by the ON DELETE CASCADE foreign keys.
app.MapDelete("/admin/users/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
{
    var me = (User)ctx.Items["user"]!;
    if (id == me.Id) return Results.BadRequest(new { error = "self_delete" });
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null) return Results.NotFound();
    var uname = user.Username;
    db.Users.Remove(user);
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "user-delete", null, uname);
    return Results.NoContent();
});

// Unlock device login lockout (admin): reset counter and clear lock.
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
    var code = SetResetCode(user);   // invalidates old password; user sets a new one with the token
    if (clearTotp == true) { user.TotpSecret = null; user.TotpConfirmed = false; } // lost authenticator -> re-enroll
    await auth.RevokeAllForUserAsync(id, ct);
    await db.SaveChangesAsync(ct);

    string? serverLang = emailCode == true ? ResolveServerLanguage(await db.ServerSettings.FirstOrDefaultAsync(ct)) : null;
    bool emailSent = emailCode == true && await EmailResetCodeAsync(email, user, code, serverLang, ct); // admin-triggered → server language

    await AuditAsync(db, ctx, "user-reset-password", null, $"{user.Username}{(emailSent ? " · token emailed" : "")}{(clearTotp == true ? " · TOTP cleared" : "")}");
    return Results.Json(new CreateUserResponse { Id = user.Id, Username = user.Username, TempPassword = temp, ResetCode = code, EmailSent = emailSent }, AgentJsonContext.Default.CreateUserResponse);
});

// Clear TOTP by itself, without password reset, for lost-authenticator re-enroll.
app.MapPost("/admin/users/{id:guid}/clear-totp", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    if (user is null) return Results.NotFound();
    user.TotpSecret = null;
    user.TotpConfirmed = false;
    await auth.RevokeAllForUserAsync(id, ct); // sign out; next sign-in sets it up again
    await db.SaveChangesAsync(ct);
    await AuditAsync(db, ctx, "user-totp-clear", null, user.Username);
    return Results.NoContent();
});

app.MapPost("/admin/users/{id:guid}/revoke-sessions", async (Guid id, HttpContext ctx, AppDbContext db, AuthService auth, CancellationToken ct) =>
{
    // Self-lockout protection: caller admin cannot immediately sign themselves out.
    var me = (User)ctx.Items["user"]!;
    if (id == me.Id) return Results.BadRequest(new { error = "self_revoke" });
    var uname = (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct))?.Username ?? id.ToString();
    await auth.RevokeAllForUserAsync(id, ct);
    await AuditAsync(db, ctx, "user-revoke-sessions", null, uname);
    return Results.NoContent();
});

// User Windows Hello devices (admin): list + revoke.
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

// User trusted ("remember this device") machines (admin): list currently-valid ones + revoke.
app.MapGet("/admin/users/{id:guid}/trusts", async (Guid id, AppDbContext db, CancellationToken ct) =>
{
    if (!await db.Users.AnyAsync(u => u.Id == id, ct)) return Results.NotFound();
    var now = DateTimeOffset.UtcNow;
    var list = await db.DeviceTrusts.Where(t => t.UserId == id && t.RevokedAt == null && t.ExpiresAt > now)
        .OrderByDescending(t => t.CreatedAt)
        .Select(t => new TrustedDeviceInfo { Id = t.Id, DeviceName = t.DeviceName, CreatedAt = t.CreatedAt, ExpiresAt = t.ExpiresAt, LastUsedAt = t.LastUsedAt })
        .ToListAsync(ct);
    return Results.Json(list, AgentJsonContext.Default.ListTrustedDeviceInfo);
});

app.MapPost("/admin/users/{id:guid}/trusts/{trustId:guid}/revoke", async (Guid id, Guid trustId, AppDbContext db, CancellationToken ct) =>
{
    var t = await db.DeviceTrusts.FirstOrDefaultAsync(x => x.Id == trustId && x.UserId == id, ct);
    if (t is null) return Results.NotFound();
    t.RevokedAt ??= DateTimeOffset.UtcNow;
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

// === Bootstrap roles (admin/operator) plus first admin user; temporary password goes to server log. ===
await SeedAsync(app);

app.Run();

// Seeds the admin/operator roles and the first admin user (temp password logged). Idempotent.
static async Task SeedAsync(WebApplication a)
{
    using var scope = a.Services.CreateScope();
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
        a.Logger.LogWarning(L.Program_BOOTSTRAPAdminCreatedUsernameAdmin, temp);
    }
}

// "mint-blob": checks the server prerequisites and, when ready, mints + prints the first bootstrap blob.
static async Task<int> RunMintBlobAsync(WebApplication a)
{
    var opt = a.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
    int missing = 0;
    void Req(bool ok, string label, string fix) { Console.WriteLine($"  [{(ok ? "OK" : "!!")}] {label}{(ok ? "" : "  -> " + fix)}"); if (!ok) missing++; }
    void Note(bool ok, string label) => Console.WriteLine($"  [{(ok ? "OK" : "--")}] {label}");

    Console.WriteLine(L.Program_MintBlobFirstRunCheck);

    // DB reachable + schema applied
    bool schemaOk = false;
    using (var scope = a.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        try { _ = await db.Roles.AnyAsync(); schemaOk = true; } catch { /* unreachable or no schema */ }
    }
    Req(schemaOk, L.Program_MintBlobDbReachableSchemaApplied, L.Program_MintBlobRunSchemaAndCheckConnection);

    if (schemaOk) { try { await SeedAsync(a); } catch { /* reported via admin check */ } }

    bool adminOk = false;
    if (schemaOk)
        using (var scope = a.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try { adminOk = await db.Users.AnyAsync(); } catch { }
        }
    Note(adminOk, L.Program_MintBlobAdminExists);

    Req(!string.IsNullOrWhiteSpace(opt.CommandSigningKeyPath) && File.Exists(opt.CommandSigningKeyPath),
        L.Program_MintBlobCommandSigningKey,
        "openssl ecparam -name prime256v1 -genkey -noout -out <path>");
    Req(File.Exists(opt.CaCertPath) && File.Exists(opt.CaKeyPath),
        L.Program_MintBlobCaCertAndKey, L.Program_MintBlobGenerateCaSeeFirstRun);
    Req(!string.IsNullOrWhiteSpace(opt.PublicUrl),
        $"PublicUrl ({(string.IsNullOrWhiteSpace(opt.PublicUrl) ? L.Program_MintBlobEmpty : opt.PublicUrl)})", L.Program_MintBlobSetServerPublicUrl);
    Note(!string.IsNullOrWhiteSpace(opt.Bastion.Host) && !string.IsNullOrWhiteSpace(opt.Bastion.HostKey),
        L.Program_MintBlobBastionHostAndHostKey);
    Note(File.Exists(opt.SecretKeyPath), L.Program_MintBlobSecretKey);

    if (missing > 0)
    {
        Console.WriteLine(L.Format(L.Program_MintBlobMissingRequiredItems, missing));
        return 2;
    }

    try
    {
        using var scope = a.Services.CreateScope();
        var enroll = scope.ServiceProvider.GetRequiredService<EnrollmentService>();
        var (raw, _) = await enroll.CreateTokenAsync(100000, null, null, "first-run", default, autoApprove: false);
        var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = opt.PublicUrl.TrimEnd('/'), Token = raw });
        Console.WriteLine(L.Program_MintBlobHeader);
        Console.WriteLine(blob);
        Console.WriteLine(L.Program_MintBlobUsage);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(L.Format(L.Program_MintBlobGenerationError, ex.Message));
        return 1;
    }
}

// Writes an audit entry best-effort; audit failure must not break the operation. Actor is the signed-in user.
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
        // Audit failure must not affect the response, but at least log it.
        try { ctx.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("Audit").LogWarning(ex, L.Program_AuditWriteErrorAction, action); } catch { }
    }
}

// Reads the Bearer session token from the Authorization header.
static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..].Trim() : null;
}

// Minimum-version gate: if the client is older than allowed, return mustUpdate with the
// current client package from the channel. null means the client is fresh enough to sign in.
// If no client package exists to update to, do not block; otherwise we could lock out the client.
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
    if (pkg is null) return null; // nothing to update to; do not brick the client

    return new LoginResponse
    {
        MustUpdate = true,
        UpdateVersion = pkg.Version,
        UpdateFileName = pkg.FileName,
        UpdateSha256 = pkg.Sha256,
    };
}

// Sets a user's role by replacing existing roles. Caller saves changes.
static async Task SetRoleAsync(AppDbContext db, Guid userId, string roleName, CancellationToken ct)
{
    var existing = await db.UserRoles.Where(ur => ur.UserId == userId).ToListAsync(ct);
    db.UserRoles.RemoveRange(existing);
    var role = await db.Roles.FirstAsync(r => r.Name == roleName, ct);
    db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
}

// Normalizes a viewer scale preference: "auto" or an integer percent 1..400. Null/empty -> "auto"; invalid -> null.
static string? NormalizeViewerScale(string? raw)
{
    var s = raw?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(s) || s == "auto") return "auto";
    return int.TryParse(s, out var pct) && pct is >= 1 and <= 400 ? pct.ToString() : null;
}

// Normalizes a viewer color-depth preference: "256" (8-bit, low-color) or "full". Null/empty/unknown -> "full".
static string NormalizeViewerColor(string? raw) =>
    string.Equals(raw?.Trim(), "256", StringComparison.OrdinalIgnoreCase) ? "256" : "full";

// Short URL-safe temporary password.
static string TempPw() =>
    Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

// 8-character reset code that is easy to type; no 0/O/1/I, uppercase letters and numbers only.
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

// === Device-level login lockout for brute-force protection. ===
const int LoginFailLockThreshold = 5;

static async Task<RemoteServer.Data.Entities.Device?> FindDeviceAsync(AppDbContext db, string? deviceId, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(deviceId) ? null : await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);

/// <summary>
/// Failed attempt: logs it for the user when known (actor=username) and for the device
/// (TargetDeviceId+detail), increments the device counter, and on the 5th attempt locks
/// the device and emails admins.
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

    // Record the attempt: actor=attempted username, TargetDeviceId=device, detail=method and reason.
    // This also saves the device counter.
    await AuditAsync(db, ctx, action, device?.Id, $"{username} · {reason}", actorOverride: username);

    if (justLocked && device is not null)
    {
        await AuditAsync(db, ctx, "device-locked", device.Id, $"{username} · {LoginFailLockThreshold}", actorOverride: "system");

        var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
        var to = s?.SupportEmail;
        if (!string.IsNullOrWhiteSpace(to))
        {
            var lang = ResolveServerLanguage(s); // server-generated, user-independent → server language
            var body = L.Format(L.Get("Program_ThereWereFailedSignIn", lang), device.Hostname, LoginFailLockThreshold) +
                       L.Format(L.Get("Program_LastAttemptedUsernameSourceIP", lang), username, PublicIpOf(ctx) ?? "-", DateTimeOffset.UtcNow) +
                       L.Get("Program_UnlockInTheClientGo", lang);
            await email.SendAsync(to!, L.Get("Program_RemoteAppClientDeviceSignInLocked", lang), body, ct);
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

/// <summary>Sets a password recovery token on the user (hash + 30 min expiry). Returns raw token; caller saves.</summary>
static string SetResetCode(RemoteServer.Data.Entities.User user)
{
    var code = ResetCode();
    user.ResetCodeHash = Sha256Hex(code);
    user.ResetCodeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
    return code;
}

/// <summary>
/// Sends the recovery token by email. Rendered in <paramref name="language"/> (the requesting client's
/// language) when provided; otherwise the server's language. true = sent successfully.
/// </summary>
// Effective server language for server-generated, user-independent messages: the configured
// ServerSettings.Language, or the OS/process culture when "auto".
static string ResolveServerLanguage(RemoteServer.Data.Entities.ServerSettings? s) =>
    string.IsNullOrWhiteSpace(s?.Language) || s.Language == "auto"
        ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
        : s.Language!;

static async Task<bool> EmailResetCodeAsync(IEmailSender email, RemoteServer.Data.Entities.User user, string code, string? language, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(user.Email)) return false;
    var lang = string.IsNullOrWhiteSpace(language) ? L.Language : language;
    var body = L.Format(L.Get("Program_PasswordRecoveryTokenForAccount", lang), user.Username, code) +
               L.Get("Program_TheTokenIsValidFor", lang) +
               L.Get("Program_IfYouDidNotRequest", lang);
    var (ok, _) = await email.SendAsync(user.Email!, L.Get("Program_RemoteAppClientPasswordRecoveryToken", lang), body, ct);
    return ok;
}

// Resolves device ID. In production nginx validates the client cert (mTLS) and forwards
// the CN in a header. Backend is reachable only from localhost/nginx, so headers are trusted.
// Fallback: direct Kestrel certificate, then ?deviceId= for dev.
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

// Agent public IP. Production: nginx X-Real-IP ($remote_addr = direct connection source,
// the address where the tunnel comes from). Fallback: first X-Forwarded-For value, then
// direct Kestrel connection in dev.
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

// Extracts CN from a DN such as "CN=abc123", "/CN=abc123", or "CN=abc,O=...".
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

// Reads incoming messages (ACKs/status). Currently logs; later can persist to commands.result.
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

        // Agent-to-server message: currently tunnel open / consent result bound to nonce.
        try
        {
            var msg = JsonSerializer.Deserialize(message.ToArray(), AgentJsonContext.Default.AgentUplinkMessage);
            if (msg is { Type: "access-result" } && !string.IsNullOrEmpty(msg.Nonce))
            {
                var entry = accessResults.RecordOutcome(msg.Nonce, msg.Outcome);
                log.LogInformation(L.Program_AccessResultDeviceOutcomeNonce, deviceId, msg.Outcome, msg.Nonce);

                // Audit: write the outcome as an audit row (who, which device, result).
                var action = msg.Outcome switch
                {
                    "granted" => "connect",        // user explicitly allowed it
                    "auto" => "connect-auto",       // without consent: consent disabled or no user present
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
                        // if Guid is unavailable, hostname falls back into Detail; otherwise Target carries it
                        DetailJson = entry?.DeviceId is null && !string.IsNullOrEmpty(entry?.Hostname) ? entry!.Hostname : null,
                    });
                    await adb.SaveChangesAsync(ct);
                }
                catch (Exception ex) { log.LogWarning(ex, L.Program_AuditWriteAccessFailed); }
            }
        }
        catch (JsonException) { log.LogDebug(L.Program_UnparseableAgentMessageDevice, deviceId); }
    }
}
