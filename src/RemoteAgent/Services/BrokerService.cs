using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RemoteAgent.Configuration;
using RemoteAgent.Tunnel;

namespace RemoteAgent.Services;

/// <summary>
/// Konzol-bróker: helyi named pipe, amin a kliens forward-tunneleket kér. Az agent a GÉP
/// enrollment-kulcsával nyit <c>ssh -L</c>-t a bástyához (admin API vagy egy cél-gép VNC
/// bástya-portja), és visszaadja a helyi loopback-portot. Így a kliensnek NINCS saját
/// SSH-kulcsa — a gép identitása a belépő, és csak BELÉPTETETT gépről indul a konzol.
/// A pipe-hoz a hitelesített helyi userek férnek; a tényleges jogosultságot a szerver-login
/// + a grantok döntik el. Csak az admin API (5000) és a tunnel-tartomány forwardolható.
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
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipe();
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { server?.Dispose(); break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bróker pipe hiba.");
                server?.Dispose();
                try { await Task.Delay(1000, stoppingToken); } catch { break; }
                continue;
            }

            // Külön taskon kezeljük a kapcsolatot, hogy közben új klienst is fogadhassunk.
            _ = HandleConnectionAsync(server, stoppingToken);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var forwards = new List<SshLocalForward>();
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == "FORWARD" && int.TryParse(parts[1], out var remotePort) && IsAllowed(remotePort))
                {
                    try
                    {
                        var fwd = new SshLocalForward(_bastion, lf.CreateLogger<SshLocalForward>());
                        await fwd.StartAsync(remotePort, ct);
                        forwards.Add(fwd);
                        await writer.WriteLineAsync($"OK {fwd.LocalPort}");
                        logger.LogInformation("Bróker forward: {Remote} -> helyi {Local}", remotePort, fwd.LocalPort);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Bróker forward sikertelen ({Port}).", remotePort);
                        await writer.WriteLineAsync("ERR forward_failed");
                    }
                }
                else
                {
                    await writer.WriteLineAsync("ERR bad_request");
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Bróker kapcsolat lezárult."); }
        finally
        {
            // A kapcsolat bontásakor minden forwardot lebontunk (a session vége).
            foreach (var f in forwards) await f.StopAsync();
            try { pipe.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Csak az admin API és a tunnel-port-tartomány forwardolható — semmi más.</summary>
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

        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, sec);
    }
}
