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

namespace RemoteAgent.Services;

/// <summary>
/// LOKÁLIS, csak-olvasható status-pipe ("RemoteAgent.status"). Csatlakozáskor egyetlen JSON
/// <see cref="StatusReport"/>-ot ír, majd zár. A kliens (és bármely helyi komponens) így
/// valós időben látja: él-e a C2, kész-e a tunnel, mikor volt utolsó szerver-kontakt.
/// SEMMI parancs, SEMMI titok — a kontroll-csatorna marad az aláírt C2.
/// </summary>
public sealed class StatusPipeService(AgentStatusState state, TunnelState tunnel, ILogger<StatusPipeService> logger) : BackgroundService
{
    public const string PipeName = "RemoteAgent.status";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Status-pipe indul (pipe: {Pipe}).", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Status-pipe létrehozása sikertelen — újrapróba 2s múlva.");
                try { await Task.Delay(2000, stoppingToken); } catch { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            catch (Exception) { await pipe.DisposeAsync(); continue; }

            _ = WriteStatusAsync(pipe, stoppingToken); // külön task; a ciklus új figyelőt nyit
        }
    }

    private async Task WriteStatusAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            var report = new StatusReport
            {
                Component = "agent",
                Version = Version,
                C2Connected = state.C2Connected,
                TunnelActive = tunnel.IsActive,
                LastServerContactUtc = state.LastServerContactUtc,
                Healthy = state.C2Connected,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(report, AgentJsonContext.Default.StatusReport);
            await pipe.WriteAsync(json, ct);
            await pipe.FlushAsync(ct);
        }
        catch (Exception ex) { logger.LogDebug(ex, "Status-pipe írás hiba."); }
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
