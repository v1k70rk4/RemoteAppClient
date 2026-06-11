using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Commands;
using RemoteAgent.Configuration;
using RemoteAgent.Enrollment;
using RemoteAgent.Services;
using RemoteAgent.Telemetry;
using RemoteAgent.Tunnel;

// "enroll" mód: telepítéskori, emberi lépés — nem a service-t indítja, hanem beléptet.
if (args is ["enroll", ..])
    return await EnrollCommand.RunAsync(args[1..]);

// "provision-vnc" mód: a TightVNC csendes telepítése + hardening (admin/SYSTEM kell).
if (args is ["provision-vnc", ..])
    return await RemoteAgent.Vnc.VncProvisioner.RunAsync(args[1..]);

var builder = Host.CreateApplicationBuilder(args);

// Windows service-ként fut (SYSTEM alatt). Konzolból is indul (debug).
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteAgent");

// Logolás: konzol + Windows EventLog (SYSTEM service-nél ez a látható napló).
builder.Logging.AddEventLog(o => o.SourceName = "RemoteAgent");

// Konfiguráció kötése.
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

// Beléptetés bekötése: ha van enrollment.json, az adja a szerver-URL-eket és a
// kliens-cert PFX-et (felülírja az appsettings placeholdereket). Ez a forrása annak,
// hová és milyen identitással csatlakozik a beléptetett gép.
builder.Services.PostConfigure<AgentOptions>(opt =>
{
    var path = Path.Combine(opt.EnrollmentDir, "enrollment.json");
    if (!File.Exists(path)) return;

    EnrollmentRecord? rec;
    try { rec = JsonSerializer.Deserialize(File.ReadAllText(path), AgentLocalJsonContext.Default.EnrollmentRecord); }
    catch { return; }
    if (rec is null || string.IsNullOrWhiteSpace(rec.ServerUrl)) return;

    var baseUrl = rec.ServerUrl.TrimEnd('/');
    opt.CommandChannel.Url = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/agent";
    opt.Telemetry.IngestUrl = baseUrl + "/api/telemetry";
    opt.ClientCertPfxPath = Path.Combine(opt.EnrollmentDir, "agent.pfx");
    if (!string.IsNullOrWhiteSpace(rec.CommandSigningPublicKey))
        opt.CommandChannel.CommandSigningPublicKey = rec.CommandSigningPublicKey;
});

// Megosztott állapot és infrastruktúra.
builder.Services.AddSingleton<CommandBus>();
builder.Services.AddSingleton<TunnelState>();
builder.Services.AddSingleton<CommandVerifier>();
builder.Services.AddSingleton<SystemInfoCollector>();

// A három háttérszolgáltatás.
builder.Services.AddHostedService<CommandChannelService>();
builder.Services.AddHostedService<TunnelOrchestratorService>();
builder.Services.AddHostedService<TelemetryService>();

var host = builder.Build();
host.Run();
return 0;
