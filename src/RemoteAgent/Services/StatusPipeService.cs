using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Admin;
using RemoteAgent.Commands;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Local, read-only status pipe ("RemoteAgent.status"). On connection it writes a single
/// JSON <see cref="StatusReport"/> and closes. The client and other local components can
/// see in real time whether C2 and tunnel are alive and when the last server contact was.
/// No commands, no secrets: the control channel remains signed C2.
/// </summary>
public sealed class StatusPipeService(AgentStatusState state, TunnelState tunnel, RemoteAgent.Telemetry.SystemInfoCollector sysInfo, Microsoft.Extensions.Options.IOptions<RemoteAgent.Configuration.AgentOptions> options, ILogger<StatusPipeService> logger) : BackgroundService
{
    public const string PipeName = "RemoteAgent.status";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private readonly string _deviceId = RemoteAgent.Telemetry.MachineIdentity.Resolve(options.Value.AgentId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(L.StatusPipeService_StatusPipeStartingPipePipe, PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex)
            {
                logger.LogDebug(ex, L.StatusPipeService_StatusPipeCreationFailedRetrying);
                try { await Task.Delay(2000, stoppingToken); } catch { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            catch (Exception) { await pipe.DisposeAsync(); continue; }

            _ = WriteStatusAsync(pipe, stoppingToken); // separate task; loop opens a fresh listener
        }
    }

    private async Task WriteStatusAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var (helper, client, vnc) = sysInfo.ComponentVersions();
            var report = new StatusReport
            {
                Component = "agent",
                Version = Version,
                HelperVersion = helper,
                ClientVersion = client,
                VncVersion = vnc,
                C2Connected = state.C2Connected,
                TunnelActive = tunnel.IsActive,
                LastServerContactUtc = state.LastServerContactUtc,
                Healthy = state.C2Connected,
                DeviceId = _deviceId,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(report, AgentJsonContext.Default.StatusReport);
            await pipe.WriteAsync(json, ct);
            await pipe.FlushAsync(ct);
        }
        catch (Exception ex) { logger.LogDebug(ex, L.StatusPipeService_StatusPipeWriteError); }
        finally { try { await pipe.DisposeAsync(); } catch { /* best effort */ } }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 4,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, sec);
    }
}
