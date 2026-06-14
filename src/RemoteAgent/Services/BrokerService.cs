using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// Console broker: a local named pipe where the client requests forward tunnels. The agent
/// opens <c>ssh -L</c> to the bastion with the device enrollment key (admin API or a target
/// device VNC bastion port), then returns a local loopback port. The client has no SSH key
/// of its own: the device identity is the credential, and the console only works on enrolled
/// devices where the agent is running.
///
/// Multi-instance listener model: there is always a fresh listener, and each connected
/// client session owns its own handler until it disconnects, even after force-kill.
/// Forwards live for the lifetime of the pipe connection. Authenticated local users can
/// reach the pipe; server login and grants decide authorization.
/// </summary>
public sealed class BrokerService(IOptions<AgentOptions> options, ILoggerFactory lf, ILogger<BrokerService> logger) : BackgroundService
{
    public const string PipeName = "RemoteAgent.broker";
    private const int AdminApiPort = 5000;
    private const int TunnelPortMin = 50000;
    private const int TunnelPortMax = 60000;

    private readonly TunnelOptions _bastion = options.Value.Tunnel;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogWarning(L.BrokerService_001, PipeName);

        // Multiple instances: always keep a fresh listener so a stuck handler, such as a force-killed
        // client, cannot block new connections. Each connection is handled by its own task.
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.BrokerService_002);
                try { await Task.Delay(2000, stoppingToken); } catch { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.BrokerService_003);
                await pipe.DisposeAsync();
                try { await Task.Delay(1000, stoppingToken); } catch { break; }
                continue;
            }

            logger.LogWarning(L.BrokerService_004);
            _ = HandleConnectionAsync(pipe, stoppingToken); // separate task; loop opens a fresh listener
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // Binary protocol: client writes int32 remote port, agent answers int32 local port
        // (0 = error). No text, line ending, or BOM ambiguity.
        var forwards = new List<SshLocalForward>();
        try
        {
            var req = new byte[4];
            while (pipe.IsConnected)
            {
                try { await pipe.ReadExactlyAsync(req, ct); }
                catch (EndOfStreamException) { break; } // client disconnected
                catch (IOException) { break; }

                int remotePort = BitConverter.ToInt32(req, 0);
                int localPort = 0;
                if (IsAllowed(remotePort))
                {
                    try
                    {
                        var fwd = new SshLocalForward(_bastion, lf.CreateLogger<SshLocalForward>());
                        await fwd.StartAsync(remotePort, ct);
                        forwards.Add(fwd);
                        localPort = fwd.LocalPort;
                        logger.LogWarning(L.BrokerService_005, remotePort, localPort);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, L.BrokerService_006, remotePort);
                        localPort = 0;
                    }
                }
                else
                {
                    logger.LogWarning(L.BrokerService_007, remotePort);
                }

                await pipe.WriteAsync(BitConverter.GetBytes(localPort), ct);
                await pipe.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex) { logger.LogWarning(ex, L.BrokerService_008); }
        finally
        {
            foreach (var f in forwards) await f.StopAsync();
            try { await pipe.DisposeAsync(); } catch { /* best effort */ }
            logger.LogWarning(L.BrokerService_009);
        }
    }

    /// <summary>Only the admin API and tunnel port range may be forwarded.</summary>
    private static bool IsAllowed(int remotePort) =>
        remotePort == AdminApiPort || (remotePort >= TunnelPortMin && remotePort < TunnelPortMax);

    private static NamedPipeServerStream CreatePipe()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        // Multiple instances are allowed so a stuck handler cannot block new connections.
        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, sec);
    }
}
