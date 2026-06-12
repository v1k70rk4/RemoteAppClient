namespace RemoteAgent.Enrollment;

/// <summary>
/// Token nélküli ön-telepítés. Ha a gép MÉG NINCS beléptetve (nincs enrollment.json),
/// de van egy bootstrap.dat (a telepítő/MSI tette le, vagy a `bootstrap` CLI-mód), akkor
/// az agent a blobból (szerver-URL + site-token) MAGÁTÓL beléptet az első induláskor.
/// A blobbal beléptetett gép a szerveren Pending-be kerül (a site-token AutoApprove=false),
/// és a kezelő a kliensben hagyja jóvá.
/// </summary>
public static class BootstrapEnroller
{
    /// <summary>
    /// Self-enroll, ha kell és lehet. A host FELÉPÜLÉSE ELŐTT fut, hogy az enrollment.json
    /// a konfiguráció-betöltéskor (PostConfigure) már létezzen. Best-effort: hibát nyel.
    /// </summary>
    public static async Task TryEnrollAsync(string outDir)
    {
        try
        {
            if (File.Exists(Path.Combine(outDir, "enrollment.json")))
                return; // már beléptetve

            var bootstrapFile = ResolveBootstrapFile(outDir);
            if (bootstrapFile is null)
                return; // nincs mit feldolgozni

            BootstrapBlob? blob;
            try { blob = BootstrapCodec.Decode(File.ReadAllText(bootstrapFile)); }
            catch { Console.Error.WriteLine("Érvénytelen bootstrap.dat — kihagyva."); return; }

            if (blob is null || string.IsNullOrWhiteSpace(blob.Url) || string.IsNullOrWhiteSpace(blob.Token))
            {
                Console.Error.WriteLine("Hiányos bootstrap blob (url/token) — kihagyva.");
                return;
            }

            Console.WriteLine($"Bootstrap self-enroll → {blob.Url}");
            var res = await EnrollCommand.EnrollCoreAsync(blob.Token, blob.Url, Environment.MachineName, outDir);
            if (res.Ok)
            {
                Console.WriteLine($"Bootstrap self-enroll OK: {res.DeviceId}");
                // A blobot elhasználtnak jelöljük (a token a szerveren AutoApprove=false → Pending).
                TryMarkUsed(bootstrapFile);
            }
            else
            {
                Console.Error.WriteLine($"Bootstrap self-enroll sikertelen: {res.ErrorCode}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Bootstrap self-enroll hiba: " + ex.Message);
        }
    }

    /// <summary>`bootstrap &lt;blob&gt;` CLI-mód: lerakja a bootstrap.dat-ot, hogy a service
    /// az első induláskor beléptessen. (A telepítő ezt vagy közvetlen fájlt is használhat.)</summary>
    public static int WriteBootstrapFile(string blob, string outDir)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            Console.Error.WriteLine("Használat: RemoteAgent bootstrap <blob>");
            return 2;
        }

        // Validálás dekódolással, mielőtt lemezre írjuk.
        BootstrapBlob? parsed;
        try { parsed = BootstrapCodec.Decode(blob); }
        catch { Console.Error.WriteLine("Érvénytelen bootstrap blob."); return 2; }
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Url) || string.IsNullOrWhiteSpace(parsed.Token))
        {
            Console.Error.WriteLine("Hiányos bootstrap blob (url/token).");
            return 2;
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "bootstrap.dat"), blob.Trim());
        Console.WriteLine($"bootstrap.dat kiírva ide: {outDir}  (a service az első induláskor beléptet)");
        return 0;
    }

    /// <summary>A bootstrap.dat helye: előbb az EnrollmentDir, majd az exe melletti (telepítő tette le).</summary>
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

    private static void TryMarkUsed(string path)
    {
        try { File.Move(path, path + ".used", overwrite: true); } catch { /* best effort */ }
    }
}
