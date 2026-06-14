using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using RemoteAgent.Commands;
using RemoteAgent.Resources;
using RemoteAgent.Security;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Agent "enroll" mode, run as an install-time interactive step. Generates a key pair
/// on the device, sends a CSR plus token to the server, and stores the returned cert
/// (PFX), CA, and enrollment.json. The private key never leaves the device.
/// Output is localized through Strings for the installing operator.
/// </summary>
public static class EnrollCommand
{
    /// <summary>Enrollment result returned to both CLI and bootstrap self-enroll.</summary>
    public sealed record EnrollResult(bool Ok, string? DeviceId, string? Thumbprint, string? ErrorCode);

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
        Console.WriteLine(Strings.EnrollContactingServer);

        var res = await EnrollCoreAsync(token, server, hostname, outDir);
        if (!res.Ok)
        {
            if (res.ErrorCode == "unreachable") Console.Error.WriteLine(Strings.EnrollServerUnreachable);
            else Console.Error.WriteLine(Strings.EnrollFailed(res.ErrorCode ?? "unknown"));
            return 1;
        }

        Console.WriteLine(Strings.EnrollSuccess);
        Console.WriteLine($"  deviceId:   {res.DeviceId}");
        Console.WriteLine($"  thumbprint: {res.Thumbprint}");
        Console.WriteLine($"  output:     {outDir}");
        return 0;
    }

    /// <summary>
    /// Actual UI-free reusable enrollment flow: generate key, CSR, and SSH key, POST /enroll,
    /// then store cert/CA/SSH-cert/enrollment.json from the response. Used by both CLI and
    /// bootstrap self-enroll. The private key never leaves the device.
    /// </summary>
    public static async Task<EnrollResult> EnrollCoreAsync(string token, string server, string hostname, string outDir)
    {
        Directory.CreateDirectory(outDir);

        // mTLS key and CSR.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrRequest = new CertificateRequest("CN=enroll", key, HashAlgorithmName.SHA256);
        string csrPem = csrRequest.CreateSigningRequestPem();

        // SSH key pair for the bastion tunnel. ssh-keygen creates it locally; private key stays on the device.
        var sshKeyPath = Path.Combine(outDir, "id_ed25519");
        string sshPublicKey = GenerateSshKey(sshKeyPath);

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
                catch { /* non-JSON error body */ }
                return new EnrollResult(false, null, null, code);
            }

            resp = await r.Content.ReadFromJsonAsync(AgentJsonContext.Default.EnrollResponse);
        }
        catch (HttpRequestException)
        {
            return new EnrollResult(false, null, null, "unreachable");
        }

        if (resp is null || string.IsNullOrEmpty(resp.Certificate))
            return new EnrollResult(false, null, null, "empty_response");

        // Combine the returned cert with the local private key and save as PFX.
        using var leaf = X509Certificate2.CreateFromPem(resp.Certificate);
        using var withKey = leaf.CopyWithPrivateKey(key);

        // Store the PFX DPAPI-protected and machine-bound, so copied blobs are unusable elsewhere.
        File.WriteAllBytes(
            Path.Combine(outDir, "agent.pfx.dat"),
            Dpapi.Protect(withKey.Export(X509ContentType.Pfx)));
        File.WriteAllText(Path.Combine(outDir, "ca.crt"), resp.CaCertificate);

        // Store the SSH cert next to the private key; OpenSSH also uses <key>-cert.pub.
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

        return new EnrollResult(true, resp.DeviceId, withKey.Thumbprint, null);
    }

    /// <summary>Generates an SSH ed25519 key pair with ssh-keygen and returns the public key.</summary>
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
            throw new InvalidOperationException(L.EnrollCommand_SshKeygenFailedSSHKey);

        TightenAcl(keyPath);
        return File.ReadAllText(keyPath + ".pub").Trim();
    }

    private static string ResolveSshKeygen() => SshTools.ResolveSshKeygen();

    /// <summary>
    /// Tightens private-key ACLs so Windows ssh.exe accepts the key under both the SYSTEM
    /// service and an admin console: owner = Administrators, inheritance disabled, and exactly
    /// SYSTEM plus Administrators have access. A concrete user ACE for the enrolling user is
    /// considered foreign under the SYSTEM service, causing "too open" and key rejection.
    /// Uses .NET FileSecurity instead of icacls to avoid localization and leftover ACE issues.
    /// </summary>
    private static void TightenAcl(string path)
    {
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // no inheritance
            sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            sec.SetOwner(admins);
            new FileInfo(path).SetAccessControl(sec);
        }
        catch { /* best effort; on failure ssh may report "too open" under SYSTEM */ }
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
