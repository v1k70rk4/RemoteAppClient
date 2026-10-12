using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Services;

/// <summary>
/// An ssh-agent for the tunnel key in the TPM. ssh.exe reaches it through a named pipe that only SYSTEM may
/// open (SSH_AUTH_SOCK names the pipe; the Windows OpenSSH client accepts a pipe path there), asks for the
/// identities, and has the handshake's data signed. The private key never leaves CNG; what crosses the pipe is
/// public key blobs and signatures. Two requests are understood, everything else is refused: the session-bind
/// extension newer clients send first, the key-management requests nobody should send here.
///
/// Served: the current tunnel key, and during a rotation's probe the staged one (<see cref="SshIdentity.Staged"/>)
/// as well. ssh signs certificate authentication with the certificate's key blob, which embeds the plain key;
/// both forms are matched. Key handles are opened on first use and dropped when they are no longer served.
/// </summary>
public sealed class SshAgentService(ILogger<SshAgentService> logger) : BackgroundService
{
    private const int MaxMessage = 256 * 1024; // a sign request carries the session id and the handshake data, kilobytes at most

    private sealed record Served(string KeyName, ECDsa Key, byte[] Blob);
    private readonly Dictionary<string, Served> _open = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(first); first = false; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, L.SshAgentService_PipeFailed);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { break; }
                continue;
            }
            try { await pipe.WaitForConnectionAsync(stoppingToken); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            catch (Exception ex) { logger.LogDebug(ex, "ssh-agent accept failed"); await pipe.DisposeAsync(); continue; }
            _ = ServeAsync(pipe, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2)); // a handshake takes seconds; a client that lingers is cut
            var lenBuf = new byte[4];
            while (!timeout.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(pipe, lenBuf, timeout.Token)) return; // closed
                var len = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
                if (len == 0 || len > MaxMessage) return;
                var msg = new byte[len];
                if (!await ReadExactlyAsync(pipe, msg, timeout.Token)) return;
                var reply = await HandleAsync(msg);
                await pipe.WriteAsync(SshKeyEncoding.Frame(reply), timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { /* the client went away */ }
        catch (Exception ex) { logger.LogDebug(ex, "ssh-agent connection ended with an error"); }
        finally { try { await pipe.DisposeAsync(); } catch { /* best effort */ } }
    }

    private async Task<byte[]> HandleAsync(byte[] msg)
    {
        switch (msg[0])
        {
            case SshKeyEncoding.AgentcRequestIdentities:
            {
                var keys = await KeysAsync();
                return keys.Count == 0 ? SshKeyEncoding.Failure() : SshKeyEncoding.IdentitiesAnswer(keys.Select(k => (k.Blob, "remoteagent")));
            }
            case SshKeyEncoding.AgentcSignRequest:
            {
                var req = SshKeyEncoding.ParseSignRequest(msg.AsSpan(1));
                if (req is null) return SshKeyEncoding.Failure();
                // The request names either the plain key or the certificate whose key it is.
                var wanted = SshKeyEncoding.PlainKeyOfCertificate(req.Value.KeyBlob) ?? req.Value.KeyBlob;
                var keys = await KeysAsync();
                var served = keys.FirstOrDefault(k => k.Blob.AsSpan().SequenceEqual(wanted));
                if (served is null) return SshKeyEncoding.Failure();
                try { return SshKeyEncoding.SignResponse(SshKeyEncoding.SignatureBlob(SshKeyStore.SignRs(served.Key, req.Value.Data))); }
                catch (CryptographicException ex)
                {
                    logger.LogWarning(ex, L.SshAgentService_SignFailed);
                    return SshKeyEncoding.Failure();
                }
            }
            default:
                return SshKeyEncoding.Failure();
        }
    }

    /// <summary>The keys served right now: the current identity's, and a staged one during a probe. Handles are
    /// opened once and closed when the key stops being served.</summary>
    private async Task<List<Served>> KeysAsync()
    {
        var wanted = new List<string>();
        if (SshIdentity.Current is { InAgent: true, KeyName: { } c }) wanted.Add(c);
        if (SshIdentity.Staged is { InAgent: true, KeyName: { } s } && !wanted.Contains(s)) wanted.Add(s);

        await _keyGate.WaitAsync();
        try
        {
            foreach (var stale in _open.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _open[stale].Key.Dispose();
                _open.Remove(stale);
            }
            var result = new List<Served>();
            foreach (var name in wanted)
            {
                if (!_open.TryGetValue(name, out var served))
                {
                    try
                    {
                        var key = SshKeyStore.Open(name);
                        if (key is null) { logger.LogWarning(L.SshAgentService_KeyMissing, name); continue; }
                        served = new Served(name, key, SshKeyStore.PublicKeyBlob(key));
                        _open[name] = served;
                    }
                    catch (CryptographicException ex) { logger.LogWarning(ex, L.SshAgentService_KeyMissing, name); continue; }
                }
                result.Add(served);
            }
            return result;
        }
        finally { _keyGate.Release(); }
    }

    private static async Task<bool> ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    private static NamedPipeServerStream CreatePipe(bool first)
    {
        // SYSTEM only: ssh.exe runs under the service, and nobody else has business with this key.
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            SshKeyStore.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, // tunnels and a probe may all be in their handshakes at once
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0, outBufferSize: 0, sec);
    }

    public override void Dispose()
    {
        foreach (var s in _open.Values) s.Key.Dispose();
        _open.Clear();
        base.Dispose();
    }
}
