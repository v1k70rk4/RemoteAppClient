using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using RemoteAgent.Admin;
using RemoteAgent.Commands;

namespace RemoteClient;

/// <summary>
/// Linux operator transport. There is no local SYSTEM agent on an operator's Linux box, so this class
/// replaces the Windows agent broker: it generates an ephemeral SSH key, signs in over public HTTPS to
/// mint a short-lived operator certificate for that key (the server gates this behind the per-account
/// keyless-operator flag + TOTP), then opens <c>ssh -L</c> forwards to the bastion with the cert -
/// exactly the local-forward role the agent broker plays on Windows. The key/cert live in a temp dir
/// for the session and are deleted on <see cref="Dispose"/>. The bastion host key is pinned per session.
/// </summary>
public sealed class LinuxOperatorTransport : IDisposable
{
    private readonly string _dir;
    private readonly string _keyPath;
    private readonly string _certPath;
    private readonly string _knownHostsPath;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly List<Process> _forwards = [];
    private readonly Lock _lock = new();

    private LinuxOperatorTransport(string dir, string keyPath, string certPath, string knownHostsPath, string host, int port, string user)
    {
        _dir = dir; _keyPath = keyPath; _certPath = certPath; _knownHostsPath = knownHostsPath;
        _host = host; _port = port; _user = user;
    }

    /// <summary>
    /// Generates an ephemeral SSH key, signs in at <c>{serverBaseUrl}/auth/login</c> with its public key,
    /// and (when the account has the keyless-operator flag) receives a short-lived cert plus bastion info.
    /// Returns the ready transport and the login response (session token, role, setup flags).
    /// Throws <see cref="AuthException"/> on a credential/TOTP error, or <see cref="InvalidOperationException"/>
    /// ("no_operator_cert") when the server returned no cert (account lacks the flag).
    /// </summary>
    public static async Task<(LinuxOperatorTransport Transport, LoginResponse Login)> LoginAsync(
        string serverBaseUrl, string username, string password, string? totp,
        string? clientVersion = null, string? channel = null,
        string? trustToken = null, bool rememberDevice = false, CancellationToken ct = default)
    {
        // Always send our version + channel: the server's minimum-version gate rejects a null/old version
        // with a "must update" response (no token/cert), which would otherwise look like a missing cert.
        clientVersion ??= System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        channel ??= "rtm";
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rac_op_" + Guid.NewGuid().ToString("N"))).FullName;
        TrySetMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var keyPath = Path.Combine(dir, "id");
        try
        {
            await GenerateKeyAsync(keyPath, ct);
            var pub = (await File.ReadAllTextAsync(keyPath + ".pub", ct)).Trim();

            using var http = new HttpClient { BaseAddress = new Uri(serverBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
            using var content = JsonContent.Create(
                new LoginRequest { Username = username, Password = password, Totp = totp, ClientVersion = clientVersion, Channel = channel, SshPublicKey = pub, TrustToken = trustToken, RememberDevice = rememberDevice },
                AgentJsonContext.Default.LoginRequest);
            using var resp = await http.PostAsync("auth/login", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string code = $"http_{(int)resp.StatusCode}";
                try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); if (!string.IsNullOrEmpty(e?.Error)) code = e.Error; }
                catch { /* non-JSON body */ }
                throw new AuthException(code);
            }

            var login = (await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.LoginResponse, ct))!;
            if (login.MustUpdate)
                throw new InvalidOperationException("client_outdated"); // server's min-version gate wants a newer console
            if (string.IsNullOrWhiteSpace(login.OperatorCert) || string.IsNullOrWhiteSpace(login.BastionHost))
                throw new InvalidOperationException("no_operator_cert"); // account is missing the keyless-operator flag

            var certPath = keyPath + "-cert.pub";
            await File.WriteAllTextAsync(certPath, login.OperatorCert.Trim() + "\n", ct);

            var port = login.BastionPort ?? 22;
            var knownHostsPath = Path.Combine(dir, "known_hosts");
            // Pin the bastion host key; include both the plain and [host]:port forms so the lookup matches any port.
            await File.WriteAllTextAsync(knownHostsPath,
                $"{login.BastionHost} {login.BastionHostKey}\n[{login.BastionHost}]:{port} {login.BastionHostKey}\n", ct);

            TrySetMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            TrySetMode(certPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            TrySetMode(knownHostsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var transport = new LinuxOperatorTransport(dir, keyPath, certPath, knownHostsPath, login.BastionHost!, port, login.BastionUser ?? "agent");
            return (transport, login);
        }
        catch
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// Public, pre-login password recovery (no tunnel/token): requests an emailed reset code. Mirrors
    /// <see cref="LoginAsync"/>'s direct HTTPS call to the public endpoint. Never throws on a server error -
    /// the endpoint is anti-enumeration and the UI must not reveal whether the account exists.
    /// </summary>
    public static async Task RequestPasswordCodeAsync(string serverBaseUrl, string username, string email, CancellationToken ct = default)
    {
        var lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        using var http = new HttpClient { BaseAddress = new Uri(serverBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        using var content = JsonContent.Create(
            new PasswordCodeRequest { Username = username, Email = email, Language = lang },
            AgentJsonContext.Default.PasswordCodeRequest);
        try { (await http.PostAsync("auth/password/request-code", content, ct)).Dispose(); }
        catch { /* anti-enumeration: swallow */ }
    }

    /// <summary>
    /// Public, pre-login password recovery (no tunnel/token): sets a new password with the emailed code.
    /// Returns (ok, errorCode) where errorCode is the server's machine code (invalid_code / weak_password / ...).
    /// </summary>
    public static async Task<(bool Ok, string? Error)> ResetPasswordAsync(string serverBaseUrl, string username, string code, string newPassword, CancellationToken ct = default)
    {
        using var http = new HttpClient { BaseAddress = new Uri(serverBaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        using var content = JsonContent.Create(
            new PasswordResetRequest { Username = username, Code = code, NewPassword = newPassword },
            AgentJsonContext.Default.PasswordResetRequest);
        using var resp = await http.PostAsync("auth/password/reset", content, ct);
        if (resp.IsSuccessStatusCode) return (true, null);
        string err = "";
        try { var e = await resp.Content.ReadFromJsonAsync(AgentJsonContext.Default.AuthError, ct); err = e?.Error ?? ""; } catch { /* non-JSON body */ }
        return (false, err);
    }

    /// <summary>
    /// Opens an <c>ssh -L</c> forward to a bastion loopback port (admin API 5000, or a device's reverse
    /// VNC/file port) and returns the local port. Each call starts its own ssh process; all are killed on
    /// <see cref="Dispose"/>. Mirrors the agent broker's <c>ForwardAsync</c> so <see cref="AdminApi"/> can
    /// use this transport unchanged.
    /// </summary>
    public async Task<int> ForwardAsync(int remotePort, CancellationToken ct = default)
    {
        int local = FreeLocalPort();
        // CreateNoWindow hides the OpenSSH console on Windows (the Lite client shells out to ssh.exe; otherwise
        // each forward pops a black console window — two per VNC connect). No effect on Linux.
        var psi = new ProcessStartInfo("ssh") { UseShellExecute = false, CreateNoWindow = true };
        string[] args =
        [
            "-i", _keyPath,
            "-o", "CertificateFile=" + _certPath,
            "-o", "UserKnownHostsFile=" + _knownHostsPath,
            "-o", "StrictHostKeyChecking=yes",
            "-o", "IdentitiesOnly=yes",
            "-o", "ExitOnForwardFailure=yes",
            "-o", "ServerAliveInterval=30",
            "-o", "BatchMode=yes",
            "-N",
            "-L", $"127.0.0.1:{local}:127.0.0.1:{remotePort}",
            "-p", _port.ToString(),
            $"{_user}@{_host}",
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("ssh_start_failed");
        lock (_lock) _forwards.Add(proc);

        // The cold ssh -L handshake can take a few seconds; wait until the local port accepts connections.
        for (int i = 0; i < 30; i++)
        {
            if (proc.HasExited) throw new InvalidOperationException("ssh_forward_failed");
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, local, ct);
                return local;
            }
            catch (SocketException) { await Task.Delay(500, ct); }
        }
        throw new InvalidOperationException("ssh_forward_timeout");
    }

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task GenerateKeyAsync(string keyPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ssh-keygen") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-t", "ed25519", "-N", "", "-q", "-f", keyPath }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ssh_keygen_failed");
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0 || !File.Exists(keyPath + ".pub"))
            throw new InvalidOperationException("ssh_keygen_failed");
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return; // never used on Windows (that path uses the SYSTEM agent broker)
        try { File.SetUnixFileMode(path, mode); } catch { /* unsupported FS: ssh-keygen already restricts the key */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var p in _forwards)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                try { p.Dispose(); } catch { /* best effort */ }
            }
            _forwards.Clear();
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
