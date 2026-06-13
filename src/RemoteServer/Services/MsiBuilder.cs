using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;

namespace RemoteServer.Services;

/// <summary>
/// Csoport-specifikus MSI legyártása Linuxon (wixl / msitools). Az MSI "buta": lerakja az
/// agent + updater exét és a group bootstrap.dat-ját a Program Files-ba, majd egy custom
/// action lefuttatja a `RemoteAgent.exe install-service`-t (ami mindkét service-t telepíti,
/// SCM-recoveryvel, és elindítja → az agent a bootstrap.dat-ból MAGÁTÓL beléptet → Pending).
/// Eltávolításkor `uninstall-service`. Opcionális osslsigncode-aláírás (ha van cert).
/// </summary>
public sealed class MsiBuilder(IOptions<ServerOptions> options, ILogger<MsiBuilder> logger)
{
    private readonly ServerOptions _opt = options.Value;

    // Stabil GUID-ok: az UpgradeCode a termékhez kötött (in-place upgrade), a komponensek fixek.
    private const string UpgradeCode = "7E2A9C40-1B3D-4C5E-9F2A-0A1B2C3D4E5F";
    private const string CompAgent = "A1A1A1A1-0001-4001-8001-000000000001";
    private const string CompUpdater = "A1A1A1A1-0001-4001-8001-000000000002";
    private const string CompBootstrap = "A1A1A1A1-0001-4001-8001-000000000003";
    private const string CompClient = "A1A1A1A1-0001-4001-8001-000000000004";
    private const string CompShortcut = "A1A1A1A1-0001-4001-8001-000000000005";

    public sealed record Result(bool Ok, string? FileName, string? Error);

    public async Task<Result> BuildAsync(
        string agentExe, string? updaterExe, string? clientExe, string bootstrapBlob,
        string version, string label, bool startMenuShortcut, string? ownerName, CancellationToken ct)
    {
        var work = Path.Combine(_opt.PackagesDir, "msi-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var bootstrapPath = Path.Combine(work, "bootstrap.dat");
            await File.WriteAllTextAsync(bootstrapPath, bootstrapBlob, ct);

            var iconPath = await TryWriteIconAsync(work, ct); // null, ha nincs beágyazott ikon

            var wxsPath = Path.Combine(work, "product.wxs");
            await File.WriteAllTextAsync(wxsPath, GenerateWxs(agentExe, updaterExe, clientExe, bootstrapPath, iconPath, SanitizeVersion(version), label, startMenuShortcut, ownerName), ct);

            var fileName = $"RemoteAppClient-{Sanitize(label)}-{SanitizeVersion(version)}.msi";
            var outPath = Path.Combine(_opt.PackagesDir, fileName);

            var (code, output) = await RunAsync("wixl", ["-o", outPath, wxsPath, "--arch", "x64"], ct);
            if (code != 0 || !File.Exists(outPath))
            {
                logger.LogWarning("wixl hiba (rc={Code}): {Output}", code, output);
                return new Result(false, null, "wixl_failed");
            }

            if (!string.IsNullOrWhiteSpace(_opt.MsiSigning.CertPath))
                await TrySignAsync(outPath, ct);

            logger.LogInformation("MSI legyártva: {File} ({Size} bájt)", fileName, new FileInfo(outPath).Length);
            return new Result(true, fileName, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MSI gyártás hiba.");
            return new Result(false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A beágyazott app.ico kiírása a munkamappába (ARP-ikonhoz). Null, ha nincs erőforrás.</summary>
    private static async Task<string?> TryWriteIconAsync(string work, CancellationToken ct)
    {
        try
        {
            await using var res = typeof(MsiBuilder).Assembly.GetManifestResourceStream("RemoteServer.app.ico");
            if (res is null) return null;
            var path = Path.Combine(work, "app.ico");
            await using var fs = File.Create(path);
            await res.CopyToAsync(fs, ct);
            return path;
        }
        catch { return null; }
    }

    private string GenerateWxs(string agentExe, string? updaterExe, string? clientExe, string bootstrapPath, string? iconPath, string version, string label, bool startMenuShortcut, string? ownerName)
    {
        bool hasClient = !string.IsNullOrWhiteSpace(clientExe);
        bool hasIcon = !string.IsNullOrWhiteSpace(iconPath);
        bool shortcut = startMenuShortcut && hasClient;
        // FONTOS: a Windows Installer NEM nyitja meg az UTF-8 (65001) codepage-ű MSI-t (1620-as hiba),
        // ezért 1252-t használunk, és a megjelenő nevet ASCII-ra hajtjuk (ékezet nélkül) — így biztos kompatibilis.
        var name = AsciiFold($"RemoteAppClient Agent ({label})");
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">""");
        sb.AppendLine($"""  <Product Name="{X(name)}" Id="*" UpgradeCode="{UpgradeCode}" Language="1033" Codepage="1252" Version="{version}" Manufacturer="RemoteAppClient">""");
        sb.AppendLine("""    <Package InstallerVersion="200" Compressed="yes" InstallScope="perMachine" Description="RemoteAppClient agent" />""");
        sb.AppendLine("""    <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." />""");
        sb.AppendLine("""    <Media Id="1" Cabinet="product.cab" EmbedCab="yes" />""");
        sb.AppendLine("""    <Directory Id="TARGETDIR" Name="SourceDir">""");
        sb.AppendLine("""      <Directory Id="ProgramFiles64Folder">""");
        sb.AppendLine("""        <Directory Id="INSTALLDIR" Name="RemoteAppClient">""");

        sb.AppendLine($"""          <Component Id="C.Agent" Guid="{CompAgent}" Win64="yes">""");
        sb.AppendLine($"""            <File Id="F.Agent" Name="RemoteAgent.exe" Source="{X(agentExe)}" KeyPath="yes" />""");
        sb.AppendLine("""          </Component>""");

        if (!string.IsNullOrWhiteSpace(updaterExe))
        {
            sb.AppendLine($"""          <Component Id="C.Updater" Guid="{CompUpdater}" Win64="yes">""");
            sb.AppendLine($"""            <File Id="F.Updater" Name="RemoteAgent.Updater.exe" Source="{X(updaterExe)}" KeyPath="yes" />""");
            sb.AppendLine("""          </Component>""");
        }

        if (hasClient)
        {
            sb.AppendLine($"""          <Component Id="C.Client" Guid="{CompClient}" Win64="yes">""");
            sb.AppendLine($"""            <File Id="F.Client" Name="RemoteClient.exe" Source="{X(clientExe!)}" KeyPath="yes" />""");
            sb.AppendLine("""          </Component>""");
        }

        sb.AppendLine($"""          <Component Id="C.Bootstrap" Guid="{CompBootstrap}" Win64="yes">""");
        sb.AppendLine($"""            <File Id="F.Bootstrap" Name="bootstrap.dat" Source="{X(bootstrapPath)}" KeyPath="yes" />""");
        sb.AppendLine("""          </Component>""");

        sb.AppendLine("""        </Directory>""");
        sb.AppendLine("""      </Directory>""");

        // Opcionális Start menü parancsikon a konzol-klienshez (perMachine → All Users start menü).
        if (shortcut)
        {
            sb.AppendLine("""      <Directory Id="ProgramMenuFolder">""");
            sb.AppendLine($"""        <Component Id="C.Shortcut" Guid="{CompShortcut}" Win64="yes">""");
            var iconAttr = hasIcon ? """ Icon="AppIcon.ico" """ : " ";
            sb.AppendLine($"""          <Shortcut Id="SC.Client" Name="RemoteAppClient" Target="[INSTALLDIR]RemoteClient.exe" WorkingDirectory="INSTALLDIR"{iconAttr}/>""");
            sb.AppendLine("""          <RegistryValue Root="HKLM" Key="Software\RemoteAppClient" Name="shortcut" Type="integer" Value="1" KeyPath="yes" />""");
            sb.AppendLine("""        </Component>""");
            sb.AppendLine("""      </Directory>""");
        }

        sb.AppendLine("""    </Directory>""");

        sb.AppendLine("""    <Feature Id="Main" Level="1">""");
        sb.AppendLine("""      <ComponentRef Id="C.Agent" />""");
        if (!string.IsNullOrWhiteSpace(updaterExe)) sb.AppendLine("""      <ComponentRef Id="C.Updater" />""");
        if (hasClient) sb.AppendLine("""      <ComponentRef Id="C.Client" />""");
        sb.AppendLine("""      <ComponentRef Id="C.Bootstrap" />""");
        if (shortcut) sb.AppendLine("""      <ComponentRef Id="C.Shortcut" />""");
        sb.AppendLine("""    </Feature>""");

        // Add/Remove Programs ikon (ha van beágyazott app.ico). A parancsikon is ezt használja.
        if (hasIcon)
        {
            sb.AppendLine($"""    <Icon Id="AppIcon.ico" SourceFile="{X(iconPath!)}" />""");
            sb.AppendLine("""    <Property Id="ARPPRODUCTICON" Value="AppIcon.ico" />""");
        }

        // A service-telepítést maga az agent végzi (mindkét service + SCM-recovery), nem az MSI deklaratívan.
        // Telepítés: a frissen telepített exe futtatása (type 18 FileKey — telepítéskor működik).
        // A megjelenített szolgáltatás-nevet az agent komponálja az átadott owner+group argokból.
        var ownerArg = string.IsNullOrWhiteSpace(ownerName) ? "" : $" --owner &quot;{X(AsciiFold(ownerName!))}&quot;";
        var groupArg = $" --group &quot;{X(AsciiFold(label))}&quot;";
        sb.AppendLine($"""    <CustomAction Id="CA.InstallSvc" FileKey="F.Agent" ExeCommand="install-service{ownerArg}{groupArg}" Execute="deferred" Impersonate="no" Return="check" />""");
        // Eltávolítás: a FileKey (type 18) eltávolításkor 2753-at ad ("nincs telepítésre jelölve"),
        // ezért az exe útvonalát property-ből (CustomActionData) adjuk át. A KVÓTÁZOTT út kezeli a szóközt.
        sb.AppendLine("""    <CustomAction Id="CA.SetUninst" Property="CA.UninstallSvc" Value="&quot;[#F.Agent]&quot;" Execute="immediate" />""");
        sb.AppendLine("""    <CustomAction Id="CA.UninstallSvc" Property="CA.UninstallSvc" ExeCommand="uninstall-service" Execute="deferred" Impersonate="no" Return="ignore" />""");
        sb.AppendLine("""    <InstallExecuteSequence>""");
        // A wixl a MajorUpgrade RemoveExistingProducts-ját az InstallInitialize ELÉ teszi (1401),
        // ami upgrade-nél „transaction not started" (2762) hibát ad. Áttesszük UTÁNA.
        sb.AppendLine("""      <RemoveExistingProducts After="InstallInitialize" />""");
        sb.AppendLine("""      <Custom Action="CA.SetUninst" Before="CA.UninstallSvc">Installed AND (REMOVE="ALL")</Custom>""");
        sb.AppendLine("""      <Custom Action="CA.UninstallSvc" Before="RemoveFiles">Installed AND (REMOVE="ALL")</Custom>""");
        sb.AppendLine("""      <Custom Action="CA.InstallSvc" After="InstallFiles">NOT Installed</Custom>""");
        sb.AppendLine("""    </InstallExecuteSequence>""");

        sb.AppendLine("""  </Product>""");
        sb.AppendLine("""</Wix>""");
        return sb.ToString();
    }

    /// <summary>Opcionális Authenticode-aláírás osslsigncode-dal (ha van cert ÉS elérhető a tool).</summary>
    private async Task TrySignAsync(string msiPath, CancellationToken ct)
    {
        try
        {
            var s = _opt.MsiSigning;
            var signed = msiPath + ".signed";
            var (code, output) = await RunAsync("osslsigncode",
                ["sign", "-pkcs12", s.CertPath, "-pass", s.Password, "-ts", s.TimestampUrl,
                 "-in", msiPath, "-out", signed], ct);
            if (code == 0 && File.Exists(signed))
            {
                File.Move(signed, msiPath, overwrite: true);
                logger.LogInformation("MSI aláírva.");
            }
            else
            {
                logger.LogWarning("MSI aláírás kihagyva/sikertelen (osslsigncode rc={Code}): {Output}", code, output);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "MSI aláírás hiba (kihagyva)."); }
    }

    /// <summary>Ékezetek/nem-ASCII eltávolítása (a Windows-kompatibilis MSI string-poolhoz).</summary>
    private static string AsciiFold(string s)
    {
        var norm = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var c in norm)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c <= 0x7F ? c : '_');
        }
        return sb.ToString();
    }

    private static async Task<(int code, string output)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, (stdout + stderr).Trim());
    }

    private static string SanitizeVersion(string v)
    {
        // MSI ProductVersion: max 3-4 numerikus tag. Levágjuk a nem szám/pont részeket.
        var clean = new string(v.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "1.0.0" : string.Join('.', parts.Take(4));
    }

    private static string Sanitize(string s)
    {
        var arr = s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        var r = new string(arr);
        return string.IsNullOrWhiteSpace(r) ? "group" : r;
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? s;
}
