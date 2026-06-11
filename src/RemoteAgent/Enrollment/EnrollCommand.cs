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
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csrRequest = new CertificateRequest("CN=enroll", key, HashAlgorithmName.SHA256);
        string csrPem = csrRequest.CreateSigningRequestPem();

        Console.WriteLine(Strings.EnrollContactingServer);
        EnrollResponse? resp;
        try
        {
            using var http = new HttpClient();
            using var r = await http.PostAsJsonAsync(
                $"{server.TrimEnd('/')}/enroll",
                new EnrollRequest { Token = token, Csr = csrPem, Hostname = hostname },
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

        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "agent.pfx"), withKey.Export(X509ContentType.Pfx));
        File.WriteAllText(Path.Combine(outDir, "ca.crt"), resp.CaCertificate);

        using var caCert = X509Certificate2.CreateFromPem(resp.CaCertificate);
        var record = new EnrollmentRecord
        {
            DeviceId = resp.DeviceId,
            CertThumbprint = withKey.Thumbprint,
            CaPinSha256 = Convert.ToHexString(SHA256.HashData(caCert.GetRawCertData())),
            CommandSigningPublicKey = resp.CommandSigningPublicKey,
            ServerUrl = server,
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
