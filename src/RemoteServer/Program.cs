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

var app = builder.Build();
app.UseWebSockets();

// === Session-auth az /admin-ra: érvényes Bearer-token kell (a transportot a gép SSH-tunnelje adja).
// A /auth/* végpontok publikusak (a tunnelen át), és maguk validálnak. Amíg a user setupja
// (jelszócsere / TOTP-enroll) nincs kész, a konzol-végpontok 403-at adnak. ===
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/admin")) { await next(); return; }

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
    ctx.Items["user"] = v.Value.User;
    await next();
});

app.MapGet("/", () => "RemoteServer up.");

// === Bejelentkezés / 2FA (a gép SSH-tunneljén át érhető el; maguk validálnak) ===
app.MapPost("/auth/login", async (HttpContext ctx, AppDbContext db, AuthService auth, SecretProtector protector, CancellationToken ct) =>
{
    LoginRequest? req;
    try { req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, AgentJsonContext.Default.LoginRequest, ct); }
    catch (JsonException) { return Results.BadRequest(); }
    if (req is null || string.IsNullOrWhiteSpace(req.Username)) return Results.BadRequest();

    var user = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
        .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);
    if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
        return Results.Json(new AuthError { Error = "invalid_credentials" }, AgentJsonContext.Default.AuthError, statusCode: 401);

    // Ha már be van állítva a TOTP, kötelező a kód.
    if (user.TotpConfirmed)
    {
        var secret = protector.TryUnprotect(user.TotpSecret);
        if (secret is null || !TotpService.Verify(secret, req.Totp ?? ""))
            return Results.Json(new AuthError { Error = string.IsNullOrWhiteSpace(req.Totp) ? "totp_required" : "totp_invalid" },
                AgentJsonContext.Default.AuthError, statusCode: 401);
    }

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
app.Map("/agent", async (HttpContext ctx, AgentConnectionRegistry registry, ILoggerFactory lf) =>
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
        await PumpIncomingAsync(socket, deviceId, log, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { /* leállás/lekapcsolódás */ }
    catch (WebSocketException ex) { log.LogDebug(ex, "WS lezárult: {Device}", deviceId); }
    finally
    {
        registry.Unregister(deviceId, socket);
        log.LogInformation("Agent lekapcsolódott: {Device}", deviceId);
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
    await sink.IngestAsync(deviceId, payload, ctx.RequestAborted);
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
app.MapGet("/admin/devices", async (AppDbContext db, AgentConnectionRegistry registry, SecretProtector protector, CancellationToken ct) =>
{
    var devices = await db.Devices.Include(d => d.Group).OrderBy(d => d.Hostname).ToListAsync(ct);
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
    if (component is not ("agent" or "updater")) return Results.BadRequest(new { error = "bad_component" });

    // Belső, ütközésmentes fájlnév: {component}-{channel}-{version}.exe
    var safeVer = version.Replace('/', '_').Replace('\\', '_');
    var fileName = $"{component}-{channel}-{safeVer}.exe";

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
    string channel, string? component, AppDbContext db, CommandService commands, CancellationToken ct) =>
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
        var reported = comp == "updater" ? d.HelperVersion : d.AgentVersion;
        if (reported is not null && reported.StartsWith(pkg.Version, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

        var data = new CommandData
        {
            UpdateVersion = pkg.Version, UpdateUrl = $"/api/updates/{pkg.FileName}",
            UpdateSha256 = pkg.Sha256, UpdateTarget = comp,
        };
        var cmd = await commands.EnqueueAsync(d.DeviceId, CommandTypes.Update, data, createdBy: null, ct);
        if (cmd is not null) sent++;
    }
    return Results.Ok(new { channel, component = comp, version = pkg.Version, devices = devices.Count, sent, skipped });
});

// Promótálás: egy csatorna aktuális csomagját a cél-csatorna aktuálisává teszi (UGYANAZ a fájl, nincs újra-feltöltés).
app.MapPost("/admin/channels/{channel}/promote", async (
    string channel, string? component, string? to, AppDbContext db, CancellationToken ct) =>
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
    string deviceId, int? remotePort, AppDbContext db, CommandService commands, CancellationToken ct) =>
{
    var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
    if (device is null) return Results.NotFound();

    var port = remotePort is > 0 ? remotePort.Value
             : device.TunnelPort ?? Random.Shared.Next(50000, 60000); // fallback régi (port nélküli) géphez
    var cmd = await commands.EnqueueAsync(
        deviceId, CommandTypes.OpenTunnel, new CommandData { RemotePort = port }, createdBy: null, ct);
    return cmd is null
        ? Results.NotFound()
        : Results.Json(
            new OpenTunnelResult { DeviceId = deviceId, RemotePort = port, Status = cmd.Status.ToString() },
            AgentJsonContext.Default.OpenTunnelResult);
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
    var raw = await enroll.CreateTokenAsync(maxUses ?? 1, expiresInHours, groupId: null, note: null, ct);
    return Results.Ok(new { token = raw, maxUses = maxUses ?? 1, expiresInHours });
});

// === Bootstrap blob: token nélküli ön-telepítéshez (site-token + szerver-URL egy stringben) ===
// A létrejövő token AutoApprove=false → a vele beléptetett gép Pending-be kerül (jóváhagyásra vár).
app.MapPost("/admin/bootstrap", async (
    EnrollmentService enroll, IOptions<ServerOptions> opt,
    string? serverUrl, Guid? groupId, int? maxUses, int? expiresInHours, CancellationToken ct) =>
{
    var url = !string.IsNullOrWhiteSpace(serverUrl) ? serverUrl : opt.Value.PublicUrl;
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "no_server_url", hint = "állítsd be a Server:PublicUrl-t vagy add meg: ?serverUrl=" });

    var raw = await enroll.CreateTokenAsync(maxUses ?? 100000, expiresInHours, groupId, note: "bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = raw });
    return Results.Ok(new { blob, url = url.TrimEnd('/'), token = raw, groupId, maxUses = maxUses ?? 100000, expiresInHours });
});

// === MSI-gyártás: egy csoporthoz, egy csatorna aktuális exéiből (wixl) ===
// Lerakja az agent+updater exét + a group bootstrap.dat-ját, és install-service-szel telepít.
app.MapPost("/admin/msi", async (
    string? group, string? channel, AppDbContext db, EnrollmentService enroll,
    MsiBuilder msi, IOptions<ServerOptions> opt, CancellationToken ct) =>
{
    var ch = string.IsNullOrWhiteSpace(channel) ? "rtm" : channel.Trim().ToLowerInvariant();

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

    // Csoport-bootstrap: AutoApprove=false site-token → a telepített gép Pending lesz.
    var token = await enroll.CreateTokenAsync(100000, expiresInHours: null, groupId, note: "msi-bootstrap", ct, autoApprove: false);
    var blob = BootstrapCodec.Encode(new BootstrapBlob { Url = url.TrimEnd('/'), Token = token });

    var res = await msi.BuildAsync(agentExe, updaterExe, blob, agentPkg.Version, label, ct);
    if (!res.Ok) return Results.Problem(res.Error ?? "msi_failed");

    return Results.Ok(new
    {
        fileName = res.FileName,
        url = $"/admin/msi/{res.FileName}",
        channel = ch, group = label, version = agentPkg.Version,
        includesUpdater = updaterExe is not null,
    });
});

// MSI letöltése (admin/localhost — a kliens SSH-tunnelen éri el).
app.MapGet("/admin/msi/{fileName}", (string fileName, IOptions<ServerOptions> opt) =>
{
    var safe = Path.GetFileName(fileName);
    var path = Path.Combine(opt.Value.PackagesDir, safe);
    return File.Exists(path) ? Results.File(path, "application/x-msi", safe) : Results.NotFound();
});

// === Bootstrap: szerepek (admin/viewer) + első admin user (ideiglenes jelszó a szerver-logba) ===
using (var scope = app.Services.CreateScope())
{
    var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    foreach (var rn in new[] { "admin", "viewer" })
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
        app.Logger.LogWarning("BOOTSTRAP admin létrehozva — felhasználónév: admin, IDEIGLENES jelszó: {Temp} (első belépéskor cserélni kell)", temp);
    }
}

app.Run();

// A Bearer session-token kiolvasása az Authorization fejlécből.
static string? BearerToken(HttpContext ctx)
{
    var h = ctx.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h["Bearer ".Length..].Trim() : null;
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
static async Task PumpIncomingAsync(WebSocket socket, string deviceId, ILogger log, CancellationToken ct)
{
    var buffer = new byte[4096];
    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
            return;
        }
        log.LogDebug("Agent üzenet {Device}: {Bytes} bájt", deviceId, result.Count);
    }
}
