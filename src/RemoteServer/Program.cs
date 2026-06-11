using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteAgent.Commands;
using RemoteAgent.Enrollment;
using RemoteAgent.Telemetry;
using RemoteServer.Configuration;
using RemoteServer.Data;
using RemoteServer.Hub;
using RemoteServer.Services;
using RemoteServer.Signing;
using RemoteServer.Telemetry;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddScoped<ITelemetrySink, DbTelemetrySink>();
builder.Services.AddScoped<CommandService>();
builder.Services.AddScoped<EnrollmentService>();

var app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => "RemoteServer up.");

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

// === Admin trigger (ideiglenes; később auth+2FA mögé) ===
// A parancs a sorba kerül és kézbesül, ha a gép online; offline-nál vár.
app.MapPost("/admin/devices/{deviceId}/open-tunnel", async (
    string deviceId, int remotePort, CommandService commands, CancellationToken ct) =>
{
    var cmd = await commands.EnqueueAsync(
        deviceId, CommandTypes.OpenTunnel, new CommandData { RemotePort = remotePort }, createdBy: null, ct);
    return cmd is null
        ? Results.NotFound(new { deviceId, known = false })
        : Results.Ok(new { deviceId, remotePort, status = cmd.Status.ToString() });
});

app.MapGet("/admin/devices/online", (AgentConnectionRegistry registry) => Results.Ok(registry.ConnectedDevices));

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

app.Run();

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
