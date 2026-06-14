using L = RemoteAgent.Localization.Strings;
namespace RemoteAgent.Enrollment;

/// <summary>
/// Tokenless self-install. If the device is not enrolled yet (no enrollment.json) but a
/// bootstrap.dat exists (written by the installer/MSI or the `bootstrap` CLI mode), the
/// agent self-enrolls on first start using the blob's server URL and site token.
/// Devices enrolled this way enter Pending on the server because the site token has
/// AutoApprove=false, and an operator approves them in the client.
/// </summary>
public static class BootstrapEnroller
{
    /// <summary>
    /// Self-enrolls when needed and possible. Runs before the host is built so enrollment.json
    /// already exists when configuration is loaded in PostConfigure. Best-effort: errors are swallowed.
    /// </summary>
    public static async Task TryEnrollAsync(string outDir)
    {
        try
        {
            if (File.Exists(Path.Combine(outDir, "enrollment.json")))
            {
                CleanupLeftovers(outDir); // remove any stray bootstrap.dat/.used from earlier installs
                return; // already enrolled
            }

            var bootstrapFile = ResolveBootstrapFile(outDir);
            if (bootstrapFile is null)
                return; // nothing to process

            BootstrapBlob? blob;
            try { blob = BootstrapCodec.Decode(File.ReadAllText(bootstrapFile)); }
            catch { Console.Error.WriteLine(L.BootstrapEnroller_InvalidBootstrapDatSkipped); return; }

            if (blob is null || string.IsNullOrWhiteSpace(blob.Url) || string.IsNullOrWhiteSpace(blob.Token))
            {
                Console.Error.WriteLine(L.BootstrapEnroller_IncompleteBootstrapBlobUrlToken);
                return;
            }

            Console.WriteLine(L.Format(L.BootstrapEnroller_BootstrapSelfEnroll, blob.Url));
            var res = await EnrollCommand.EnrollCoreAsync(blob.Token, blob.Url, Environment.MachineName, outDir);
            if (res.Ok)
            {
                Console.WriteLine(L.Format(L.BootstrapEnroller_BootstrapSelfEnrollOK, res.DeviceId));
                // Delete the blob once enrolled: enrollment.json already gates re-processing, the file is
                // no longer needed, it keeps the site token off disk, and it leaves nothing for MSI to orphan.
                TryDeleteUsed(bootstrapFile);
            }
            else
            {
                Console.Error.WriteLine(L.Format(L.BootstrapEnroller_BootstrapSelfEnrollFailed, res.ErrorCode));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(L.BootstrapEnroller_BootstrapSelfEnrollError + ex.Message);
        }
    }

    /// <summary>`bootstrap &lt;blob&gt;` CLI mode: writes bootstrap.dat so the service enrolls on first start.
    /// Installers can also place that file directly.</summary>
    public static int WriteBootstrapFile(string blob, string outDir)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            Console.Error.WriteLine(L.BootstrapEnroller_UsageRemoteAgentBootstrapBlob);
            return 2;
        }

        // Validate by decoding before writing to disk.
        BootstrapBlob? parsed;
        try { parsed = BootstrapCodec.Decode(blob); }
        catch { Console.Error.WriteLine(L.BootstrapEnroller_InvalidBootstrapBlob); return 2; }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Url) || string.IsNullOrWhiteSpace(parsed.Token))
        {
            Console.Error.WriteLine(L.BootstrapEnroller_IncompleteBootstrapBlobUrlToken_2);
            return 2;
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "bootstrap.dat"), blob.Trim());
        Console.WriteLine(L.Format(L.BootstrapEnroller_BootstrapDatWrittenToThe, outDir));
        return 0;
    }

    /// <summary>bootstrap.dat location: first EnrollmentDir, then next to the executable as placed by the installer.</summary>
    private static string? ResolveBootstrapFile(string outDir)
    {
        var inData = Path.Combine(outDir, "bootstrap.dat");
        if (File.Exists(inData)) return inData;

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            var coLocated = Path.Combine(exeDir, "bootstrap.dat");
            if (File.Exists(coLocated)) return coLocated;
        }
        return null;
    }

    private static void TryDeleteUsed(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
        // Clean up the legacy ".used" rename from older agents, if present.
        try { File.Delete(path + ".used"); } catch { /* best effort */ }
    }

    /// <summary>Removes leftover bootstrap.dat/.used from both the data dir and the executable dir.</summary>
    private static void CleanupLeftovers(string outDir)
    {
        var dirs = new List<string> { outDir };
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir)) dirs.Add(exeDir);
        foreach (var dir in dirs)
            foreach (var name in new[] { "bootstrap.dat", "bootstrap.dat.used" })
            {
                try { File.Delete(Path.Combine(dir, name)); } catch { /* best effort */ }
            }
    }
}
