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
/// SSH-kulcsa — a gép identitása a belépő, és csak BELÉPTETETT gépen (ahol fut az agent)
/// működik a konzol.
///
/// EGY-kapcsolatos modell: mindig PONTOSAN egy figyelő instance van; egy kliens-session
/// alatt foglalt, a kliens bontásakor (akár force-kill) felszabadul és új figyelő jön.
/// Egy gépen jellemzően egy konzol fut. A forwardok a kapcsolat élettartamáig élnek.
/// A pipe-hoz hitelesített helyi userek férnek; a jogosultságot a szerver-login + grantok döntik el.
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
        logger.LogWarning("Konzol-bróker indul (pipe: {Pipe}).", PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bróker pipe létrehozása sikertelen — újrapróba 2s múlva.");
                try { await Task.Delay(2000, stoppingToken); } catch { break; }
                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                logger.LogWarning("Bróker: kliens csatlakozott.");
                await HandleConnectionAsync(pipe, stoppingToken);
                logger.LogWarning("Bróker: kliens lecsatlakozott, forwardok lebontva.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Bróker kapcsolat hiba."); }
            finally { try { await pipe.DisposeAsync(); } catch { /* best effort */ } }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var forwards = new List<SshLocalForward>();
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(pipe, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };

            string? line;
            while (pipe.IsConnected && (line = await reader.ReadLineAsync(ct)) is not null)
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
                        logger.LogWarning("Bróker forward OK: bástya {Remote} -> helyi {Local}.", remotePort, fwd.LocalPort);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Bróker forward SIKERTELEN ({Port}) — lásd az ssh -L sorokat.", remotePort);
                        await writer.WriteLineAsync("ERR forward_failed");
                    }
                }
                else
                {
                    await writer.WriteLineAsync("ERR bad_request");
                }
            }
        }
        catch (IOException) { /* a kliens bontotta — normális */ }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { logger.LogDebug(ex, "Bróker handler hiba."); }
        finally
        {
            foreach (var f in forwards) await f.StopAsync();
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

        // Egyetlen instance (maxNumberOfServerInstances: 1) — egy konzol-session egyszerre.
        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, sec);
    }
}
