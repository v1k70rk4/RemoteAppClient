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

        // TÖBB instance: mindig van új figyelő, így egy beragadt kezelő (pl. force-killed kliens)
        // sem blokkolja az új csatlakozásokat. Minden kapcsolatot külön task kezel.
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
            }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bróker accept hiba.");
                await pipe.DisposeAsync();
                try { await Task.Delay(1000, stoppingToken); } catch { break; }
                continue;
            }

            logger.LogWarning("Bróker: kliens csatlakozott.");
            _ = HandleConnectionAsync(pipe, stoppingToken); // külön task; a ciklus új figyelőt nyit
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // BINÁRIS protokoll: a kliens int32 távoli portot ír, mi int32 helyi portot válaszolunk
        // (0 = hiba). Nincs szöveg/sorvég/BOM gond.
        var forwards = new List<SshLocalForward>();
        try
        {
            var req = new byte[4];
            while (pipe.IsConnected)
            {
                try { await pipe.ReadExactlyAsync(req, ct); }
                catch (EndOfStreamException) { break; } // a kliens bontotta
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
                        logger.LogWarning("Bróker forward OK: bástya {Remote} -> helyi {Local}.", remotePort, localPort);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Bróker forward SIKERTELEN ({Port}) — lásd az 'ssh -L:' sorokat.", remotePort);
                        localPort = 0;
                    }
                }
                else
                {
                    logger.LogWarning("Bróker: nem engedélyezett port kérve ({Port}).", remotePort);
                }

                await pipe.WriteAsync(BitConverter.GetBytes(localPort), ct);
                await pipe.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* leállás */ }
        catch (Exception ex) { logger.LogWarning(ex, "Bróker handler hiba."); }
        finally
        {
            foreach (var f in forwards) await f.StopAsync();
            try { await pipe.DisposeAsync(); } catch { /* best effort */ }
            logger.LogWarning("Bróker: kliens lecsatlakozott, forwardok lebontva.");
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

        // Több instance engedélyezett (egy beragadt kezelő ne blokkoljon új csatlakozást).
        return NamedPipeServerStreamAcl.Create(
            PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 8,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 0, outBufferSize: 0, sec);
    }
}
