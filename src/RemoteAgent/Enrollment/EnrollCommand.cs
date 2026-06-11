using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using RemoteAgent.Commands;
using RemoteAgent.Resources;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Az agent "enroll" módja (telepítéskor, emberi lépés). A gépen kulcspárt generál,
/// CSR-t küld a szervernek a tokennel, és eltárolja a visszakapott certet (PFX) + a
/// CA-t + az enrollment.json-t. A privát kulcs SOHA nem hagyja el a gépet.
/// A kimenet lokalizált (Strings) — a telepítő ember nyelvén.
/// </summary>
public static class EnrollCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (token, server, hostname, outDir) = ParseArgs(args);

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine(Strings.EnrollMissingToken);
            Console.Error.WriteLine(Strings.EnrollUsage);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(server))
        {
            Console.Error.WriteLine(Strings.EnrollUsage);
            return 2;
        }

        Console.WriteLine(Strings.EnrollGeneratingKeys);
        Directory.CreateDirectory(outDir);

        // mTLS kulcs + CSR.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrRequest = new CertificateRequest("CN=enroll", key, HashAlgorithmName.SHA256);
        string csrPem = csrRequest.CreateSigningRequestPem();

        // SSH kulcspár a bástya-tunnelhez (ssh-keygen — a privát kulcs a gépen marad).
        var sshKeyPath = Path.Combine(outDir, "id_ed25519");
        string sshPublicKey = GenerateSshKey(sshKeyPath);

        Console.WriteLine(Strings.EnrollContactingServer);
        EnrollResponse? resp;
        try
        {
            using var http = new HttpClient();
            using var r = await http.PostAsJsonAsync(
                $"{server.TrimEnd('/')}/enroll",
                new EnrollRequest { Token = token, Csr = csrPem, Hostname = hostname, SshPublicKey = sshPublicKey },
                AgentJsonContext.Default.EnrollRequest);

            if (!r.IsSuccessStatusCode)
            {
                string code = $"http_{(int)r.StatusCode}";
                try
                {
                    var err = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollError);
                    if (!string.IsNullOrEmpty(err?.Code)) code = err.Code;
                }
                catch { /* nem JSON hibatest */ }
                Console.Error.WriteLine(Strings.EnrollFailed(code));
                return 1;
            }

            resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollResponse);
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(Strings.EnrollServerUnreachable);
            return 1;
        }

        if (resp is null || string.IsNullOrEmpty(resp.Certificate))
        {
            Console.Error.WriteLine(Strings.EnrollFailed("empty_response"));
            return 1;
        }

        // A visszakapott cert + a helyi privát kulcs összefűzése, PFX-be mentés.
        using var leaf = X509Certificate2.CreateFromPem(resp.Certificate);
        using var withKey = leaf.CopyWithPrivateKey(key);

        File.WriteAllBytes(Path.Combine(outDir, "agent.pfx"), withKey.Export(X509ContentType.Pfx));
        File.WriteAllText(Path.Combine(outDir, "ca.crt"), resp.CaCertificate);

        // Az SSH-cert a privát kulcs mellé (OpenSSH a <kulcs>-cert.pub-ot is használja).
        if (!string.IsNullOrWhiteSpace(resp.SshCertificate))
            File.WriteAllText(sshKeyPath + "-cert.pub", resp.SshCertificate.Trim() + "\n");

        using var caCert = X509Certificate2.CreateFromPem(resp.CaCertificate);
        var record = new EnrollmentRecord
        {
            DeviceId = resp.DeviceId,
            CertThumbprint = withKey.Thumbprint,
            CaPinSha256 = Convert.ToHexString(SHA256.HashData(caCert.GetRawCertData())),
            CommandSigningPublicKey = resp.CommandSigningPublicKey,
            ServerUrl = server,
            BastionHost = resp.BastionHost,
            BastionPort = resp.BastionPort,
            BastionUser = resp.BastionUser,
            BastionHostKey = resp.BastionHostKey,
            EnrolledAtUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(
            Path.Combine(outDir, "enrollment.json"),
            JsonSerializer.Serialize(record, AgentLocalJsonContext.Default.EnrollmentRecord));

        Console.WriteLine(Strings.EnrollSuccess);
        Console.WriteLine($"  deviceId:   {resp.DeviceId}");
        Console.WriteLine($"  thumbprint: {withKey.Thumbprint}");
        Console.WriteLine($"  output:     {outDir}");
        return 0;
    }

    /// <summary>SSH ed25519 kulcspár generálása ssh-keygennel; visszaadja a publikus kulcsot.</summary>
    private static string GenerateSshKey(string keyPath)
    {
        foreach (var p in new[] { keyPath, keyPath + ".pub", keyPath + "-cert.pub" })
            if (File.Exists(p)) File.Delete(p);

        var psi = new ProcessStartInfo(ResolveSshKeygen())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(keyPath);
        psi.ArgumentList.Add("-N"); psi.ArgumentList.Add("");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add("remoteagent");

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0 || !File.Exists(keyPath + ".pub"))
            throw new InvalidOperationException("ssh-keygen sikertelen (SSH kulcs generálás).");

        TightenAcl(keyPath);
        return File.ReadAllText(keyPath + ".pub").Trim();
    }

    private static string ResolveSshKeygen()
    {
        var sys = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
        return File.Exists(sys) ? sys : "ssh-keygen";
    }

    /// <summary>A privát kulcs ACL-jét leszűkíti a futtató fiókra (a Windows ssh különben elutasítja).</summary>
    private static void TightenAcl(string path)
    {
        try
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var psi = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/inheritance:r");
            psi.ArgumentList.Add("/grant:r");
            psi.ArgumentList.Add($"{user}:F");
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
        }
        catch { /* best effort */ }
    }

    private static (string? Token, string? Server, string Hostname, string OutDir) ParseArgs(string[] args)
    {
        string? token = null, server = null, hostname = null, outDir = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--token": token = args[++i]; break;
                case "--server": server = args[++i]; break;
                case "--hostname": hostname = args[++i]; break;
                case "--out": outDir = args[++i]; break;
            }
        }
        return (token, server,
            hostname ?? Environment.MachineName,
            outDir ?? @"C:\ProgramData\RemoteAgent");
    }
}
