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

RemoteAgent.Globalization.RuntimeLanguage.ApplyFromSharedSettings();

// "enroll" mód: telepítéskori, emberi lépés — nem a service-t indítja, hanem beléptet.
if (args is ["enroll", ..])
    return await EnrollCommand.RunAsync(args[1..]);

// "provision-vnc" mód: a TightVNC csendes telepítése + hardening (admin/SYSTEM kell).
if (args is ["provision-vnc", ..])
    return await RemoteAgent.Vnc.VncProvisioner.RunAsync(args[1..]);

// Helyi VNC-zár (admin/SYSTEM kell): a távoli elérés letiltása/feloldása ezen a gépen.
if (args is ["vnc-lock", ..]) return RemoteAgent.Vnc.VncLock.Lock();
if (args is ["vnc-unlock", ..]) return RemoteAgent.Vnc.VncLock.Unlock();

// Service telepítése/eltávolítása (admin kell). Opcionális: --owner "<név>" --group "<csoport>"
// → a megjelenített szolgáltatás-név "{owner} RemoteAppClient Agent ({group})".
if (args is ["install-service", ..])
    return await RemoteAgent.ServiceControl.InstallAsync(ArgVal(args, "--owner"), ArgVal(args, "--group"));
if (args is ["uninstall-service", ..]) return await RemoteAgent.ServiceControl.UninstallAsync();

static string? ArgVal(string[] a, string flag)
{
    var i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

// "bootstrap <blob>" mód: token nélküli ön-telepítés előkészítése (lerakja a bootstrap.dat-ot).
if (args is ["bootstrap", var bootstrapBlob, ..])
    return RemoteAgent.Enrollment.BootstrapEnroller.WriteBootstrapFile(bootstrapBlob, @"C:\ProgramData\RemoteAgent");

var builder = Host.CreateApplicationBuilder(args);

// Windows service-ként fut (SYSTEM alatt). Konzolból is indul (debug).
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteAgent");

// Egy háttérszolgáltatás hibája NE állítsa le az egész agentet (pl. hiányzó cert
// beléptetés előtt) — a szolgáltatások maguk kezelik/újrapróbálják.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

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

    if (!string.IsNullOrWhiteSpace(rec.DeviceId)) opt.AgentId = rec.DeviceId; // a szerver-oldali DeviceId (cert CN)
    var baseUrl = rec.ServerUrl.TrimEnd('/');
    opt.CommandChannel.Url = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/agent";
    opt.Telemetry.IngestUrl = baseUrl + "/api/telemetry";
    opt.ClientCertPfxPath = Path.Combine(opt.EnrollmentDir, "agent.pfx.dat");
    if (!string.IsNullOrWhiteSpace(rec.CommandSigningPublicKey))
        opt.CommandChannel.CommandSigningPublicKey = rec.CommandSigningPublicKey;

    // Bástya-tunnel konfig az enrollmentből.
    if (!string.IsNullOrWhiteSpace(rec.BastionHost))
    {
        opt.Tunnel.BastionHost = rec.BastionHost;
        opt.Tunnel.BastionPort = rec.BastionPort;
        opt.Tunnel.BastionUser = rec.BastionUser;
        opt.Tunnel.BastionHostKey = rec.BastionHostKey;
        opt.Tunnel.PrivateKeyPath = Path.Combine(opt.EnrollmentDir, "id_ed25519");
        opt.Tunnel.CertificatePath = Path.Combine(opt.EnrollmentDir, "id_ed25519-cert.pub");
    }
});

// Megosztott állapot és infrastruktúra.
builder.Services.AddSingleton<CommandBus>();
builder.Services.AddSingleton<TunnelState>();
builder.Services.AddSingleton<AgentStatusState>();
builder.Services.AddSingleton<AgentUplink>();
builder.Services.AddSingleton<CommandVerifier>();
builder.Services.AddSingleton<SystemInfoCollector>();
builder.Services.AddSingleton<RemoteAgent.Update.UpdateInstaller>();

// A három háttérszolgáltatás.
builder.Services.AddHostedService<CommandChannelService>();
builder.Services.AddHostedService<TunnelOrchestratorService>();
builder.Services.AddHostedService<TelemetryService>();
builder.Services.AddHostedService<VncProvisioningService>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<HelperUpdateWatcher>();
builder.Services.AddHostedService<BrokerService>();
builder.Services.AddHostedService<StatusPipeService>();

// Token nélküli ön-telepítés: ha nincs enrollment, de van bootstrap.dat, beléptet MOST —
// a host felépülése (és a PostConfigure enrollment.json-olvasása) ELŐTT.
var enrollDir = builder.Configuration["Agent:EnrollmentDir"];
await RemoteAgent.Enrollment.BootstrapEnroller.TryEnrollAsync(
    string.IsNullOrWhiteSpace(enrollDir) ? @"C:\ProgramData\RemoteAgent" : enrollDir);

var host = builder.Build();
host.Run();
return 0;
