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

// "enroll" mode: install-time interactive step; enrolls the device instead of starting the service.
if (args is ["enroll", ..])
    return await EnrollCommand.RunAsync(args[1..]);

// "provision-vnc" mode: silent TightVNC install and hardening; requires admin/SYSTEM.
if (args is ["provision-vnc", ..])
    return await RemoteAgent.Vnc.VncProvisioner.RunAsync(args[1..]);

// Local VNC lock (admin/SYSTEM): disables or enables remote access on this device.
if (args is ["vnc-lock", ..]) return RemoteAgent.Vnc.VncLock.Lock();
if (args is ["vnc-unlock", ..]) return RemoteAgent.Vnc.VncLock.Unlock();

// "remove-vnc" mode: force-remove TightVNC (service + files + registry). Used by the MSI uninstall.
if (args is ["remove-vnc", ..]) return RemoteAgent.Vnc.VncProvisioner.Remove();

// Service install/uninstall (admin). Optional: --owner "<name>" --group "<group>".
// Display name becomes "{owner} RemoteAppClient Agent ({group})".
if (args is ["install-service", ..])
    return await RemoteAgent.ServiceControl.InstallAsync(ArgVal(args, "--owner"), ArgVal(args, "--group"));
if (args is ["uninstall-service", ..]) return await RemoteAgent.ServiceControl.UninstallAsync();

static string? ArgVal(string[] a, string flag)
{
    var i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

// The wss443 transport tunnels SSH over a WebSocket at the server's /ssh path, which mirrors the
// C2 URL (…/agent → …/ssh). Empty when no C2 URL is configured.
static string DeriveSshUrl(string c2Url)
{
    if (string.IsNullOrWhiteSpace(c2Url)) return "";
    try { return new UriBuilder(new Uri(c2Url)) { Path = "/ssh" }.Uri.ToString(); }
    catch { return c2Url.Replace("/agent", "/ssh"); }
}

// "bootstrap <blob>" mode: prepares tokenless self-install by writing bootstrap.dat.
if (args is ["bootstrap", var bootstrapBlob, ..])
    return RemoteAgent.Enrollment.BootstrapEnroller.WriteBootstrapFile(bootstrapBlob, @"C:\ProgramData\RemoteAgent");

var builder = Host.CreateApplicationBuilder(args);

// Runs as a Windows service under SYSTEM. It can also run from console for debugging.
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteAgent");

// A background-service failure should not stop the whole agent, for example while the
// certificate is missing before enrollment; services handle and retry their own failures.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Logging: console plus Windows EventLog, which is the visible log for the SYSTEM service.
builder.Logging.AddEventLog(o => o.SourceName = "RemoteAgent");

// Bind configuration.
builder.Services.Configure<AgentOptions>(
    builder.Configuration.GetSection(AgentOptions.SectionName));

// Enrollment binding: when enrollment.json exists, it supplies server URLs and the
// client-cert PFX, overriding appsettings placeholders. This defines where and with
// which identity an enrolled device connects.
builder.Services.PostConfigure<AgentOptions>(opt =>
{
    var path = Path.Combine(opt.EnrollmentDir, "enrollment.json");
    if (!File.Exists(path)) return;

    EnrollmentRecord? rec;
    try { rec = JsonSerializer.Deserialize(File.ReadAllText(path), AgentLocalJsonContext.Default.EnrollmentRecord); }
    catch { return; }
    if (rec is null || string.IsNullOrWhiteSpace(rec.ServerUrl)) return;

    if (!string.IsNullOrWhiteSpace(rec.DeviceId)) opt.AgentId = rec.DeviceId; // server-side DeviceId (cert CN)
    var baseUrl = rec.ServerUrl.TrimEnd('/');
    opt.CommandChannel.Url = baseUrl.Replace("https://", "wss://").Replace("http://", "ws://") + "/agent";
    opt.Telemetry.IngestUrl = baseUrl + "/api/telemetry";
    opt.ClientCertPfxPath = Path.Combine(opt.EnrollmentDir, "agent.pfx.dat");
    if (!string.IsNullOrWhiteSpace(rec.CommandSigningPublicKey))
        opt.CommandChannel.CommandSigningPublicKey = rec.CommandSigningPublicKey;

    // Bastion tunnel config from enrollment.
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

// Shared state and infrastructure.
builder.Services.AddSingleton<CommandBus>();
builder.Services.AddSingleton<TunnelState>();
builder.Services.AddSingleton<TransportState>(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    return new TransportState(o.Tunnel.BastionTransport, DeriveSshUrl(o.CommandChannel.Url),
        o.ClientCertPfxPath, o.CommandChannel.ClientCertThumbprint, o.CommandChannel.ServerCertPinSha256);
});
builder.Services.AddSingleton<AgentStatusState>();
builder.Services.AddSingleton<AgentUplink>();
builder.Services.AddSingleton<CommandVerifier>();
builder.Services.AddSingleton<SystemInfoCollector>();
builder.Services.AddSingleton<RemoteAgent.Update.UpdateInstaller>();

// Background services.
builder.Services.AddHostedService<CommandChannelService>();
builder.Services.AddHostedService<TunnelOrchestratorService>();
builder.Services.AddHostedService<TelemetryService>();
builder.Services.AddHostedService<VncProvisioningService>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<HelperUpdateWatcher>();
builder.Services.AddHostedService<BrokerService>();
builder.Services.AddHostedService<StatusPipeService>();

// Tokenless self-install: when there is no enrollment but bootstrap.dat exists, enroll now,
// before the host is built and before PostConfigure reads enrollment.json.
var enrollDir = builder.Configuration["Agent:EnrollmentDir"];
await RemoteAgent.Enrollment.BootstrapEnroller.TryEnrollAsync(
    string.IsNullOrWhiteSpace(enrollDir) ? @"C:\ProgramData\RemoteAgent" : enrollDir);

var host = builder.Build();
host.Run();
return 0;
