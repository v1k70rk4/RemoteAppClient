using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteServer.Configuration;
using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Services;

/// <summary>
/// Builds group-specific MSI packages on Linux with wixl/msitools. The MSI is simple:
/// places agent + updater executables and the group bootstrap.dat under Program Files,
/// then runs `RemoteAgent.exe install-service` as a custom action. That installs both
/// services with SCM recovery, starts them, and the agent self-enrolls from bootstrap.dat
/// into Pending. Uninstall runs `uninstall-service`. Optional osslsigncode signing.
/// </summary>
public sealed class MsiBuilder(IOptions<ServerOptions> options, ILogger<MsiBuilder> logger)
{
    private readonly ServerOptions _opt = options.Value;

    // Stable GUIDs: UpgradeCode belongs to the product for in-place upgrade; components are fixed.
    private const string UpgradeCode = "7E2A9C40-1B3D-4C5E-9F2A-0A1B2C3D4E5F";
    private const string CompAgent = "A1A1A1A1-0001-4001-8001-000000000001";
    private const string CompUpdater = "A1A1A1A1-0001-4001-8001-000000000002";
    private const string CompBootstrap = "A1A1A1A1-0001-4001-8001-000000000003";
    private const string CompClient = "A1A1A1A1-0001-4001-8001-000000000004";
    private const string CompShortcut = "A1A1A1A1-0001-4001-8001-000000000005";
    private const string CompVnc = "A1A1A1A1-0001-4001-8001-000000000006";

    public sealed record Result(bool Ok, string? FileName, string? Error);

    public async Task<Result> BuildAsync(
        string agentExe, string? updaterExe, string? clientExe, string bootstrapBlob,
        string version, string label, bool startMenuShortcut, string? ownerName, string? vncMsi, CancellationToken ct)
    {
        var work = Path.Combine(_opt.PackagesDir, "msi-build", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var bootstrapPath = Path.Combine(work, "bootstrap.dat");
            await File.WriteAllTextAsync(bootstrapPath, bootstrapBlob, ct);

            var iconPath = await TryWriteIconAsync(work, ct); // null when no embedded icon exists

            var wxsPath = Path.Combine(work, "product.wxs");
            var vncSource = !string.IsNullOrWhiteSpace(vncMsi) && File.Exists(vncMsi) ? vncMsi : null;
            await File.WriteAllTextAsync(wxsPath, GenerateWxs(agentExe, updaterExe, clientExe, bootstrapPath, iconPath, SanitizeVersion(version), label, startMenuShortcut, ownerName, vncSource), ct);

            // File name: "{Owner}_RemoteAppClient_{group}.msi", accent-free for portability.
            var ownerTok = string.IsNullOrWhiteSpace(ownerName) ? "" : FileToken(ownerName) + "_";
            var groupTok = FileToken(label) is { Length: > 0 } g ? g : "group";
            var fileName = $"{ownerTok}RemoteAppClient_{groupTok}.msi";
            var outPath = Path.Combine(_opt.PackagesDir, fileName);

            var (code, output) = await RunAsync("wixl", ["-o", outPath, wxsPath, "--arch", "x64"], ct);
            if (code != 0 || !File.Exists(outPath))
            {
                logger.LogWarning(L.MsiBuilder_WixlErrorRcCodeOutput, code, output);
                return new Result(false, null, "wixl_failed");
            }

            if (!string.IsNullOrWhiteSpace(_opt.MsiSigning.CertPath))
                await TrySignAsync(outPath, ct);

            logger.LogInformation(L.MsiBuilder_MSIBuiltFileSizeBytes, fileName, new FileInfo(outPath).Length);
            return new Result(true, fileName, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, L.MsiBuilder_MSIBuildError);
            return new Result(false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Writes embedded app.ico to the work directory for ARP icon. Null when resource is missing.</summary>
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

    private string GenerateWxs(string agentExe, string? updaterExe, string? clientExe, string bootstrapPath, string? iconPath, string version, string label, bool startMenuShortcut, string? ownerName, string? vncMsi)
    {
        bool hasClient = !string.IsNullOrWhiteSpace(clientExe);
        bool hasIcon = !string.IsNullOrWhiteSpace(iconPath);
        bool hasVnc = !string.IsNullOrWhiteSpace(vncMsi);
        bool shortcut = startMenuShortcut && hasClient;
        // Important: Windows Installer does not open UTF-8 (65001) MSI databases (error 1620),
        // so use 1252 and fold display names to ASCII for compatibility.
        // Installed program name: "{Owner} RemoteAppClient ({group})" (owner prefix dropped when unset).
        var name = AsciiFold(string.IsNullOrWhiteSpace(ownerName)
            ? $"RemoteAppClient ({label})"
            : $"{ownerName} RemoteAppClient ({label})");
        // Publisher (ARP "Manufacturer"): the owner when set, otherwise the product name.
        var manufacturer = AsciiFold(string.IsNullOrWhiteSpace(ownerName) ? "RemoteAppClient" : ownerName!);
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">""");
        sb.AppendLine($"""  <Product Name="{X(name)}" Id="*" UpgradeCode="{UpgradeCode}" Language="1033" Codepage="1252" Version="{version}" Manufacturer="{X(manufacturer)}">""");
        sb.AppendLine("""    <Package InstallerVersion="200" Compressed="yes" InstallScope="perMachine" Description="RemoteAppClient agent" />""");
        sb.AppendLine("""    <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." />""");
        sb.AppendLine("""    <Media Id="1" Cabinet="product.cab" EmbedCab="yes" />""");
        sb.AppendLine("""    <Directory Id="TARGETDIR" Name="SourceDir">""");
        sb.AppendLine("""      <Directory Id="ProgramFiles64Folder">""");
        sb.AppendLine("""        <Directory Id="INSTALLDIR" Name="RemoteAppClient">""");

        sb.AppendLine($"""          <Component Id="C.Agent" Guid="{CompAgent}" Win64="yes">""");
        sb.AppendLine($"""            <File Id="F.Agent" Name="RemoteAgent.exe" Source="{X(agentExe)}" KeyPath="yes" />""");
        // Stop + delete the service on uninstall (declarative, reliable) so the exe is unlocked before RemoveFiles.
        sb.AppendLine("""            <ServiceControl Id="SC.Agent" Name="RemoteAgent" Stop="uninstall" Remove="uninstall" Wait="yes" />""");
        sb.AppendLine("""          </Component>""");

        if (!string.IsNullOrWhiteSpace(updaterExe))
        {
            sb.AppendLine($"""          <Component Id="C.Updater" Guid="{CompUpdater}" Win64="yes">""");
            sb.AppendLine($"""            <File Id="F.Updater" Name="RemoteAgent.Updater.exe" Source="{X(updaterExe)}" KeyPath="yes" />""");
            sb.AppendLine("""            <ServiceControl Id="SC.Updater" Name="RemoteAgent.Updater" Stop="uninstall" Remove="uninstall" Wait="yes" />""");
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
        // Remove the (now empty) install folder on uninstall. wixl ignores the Directory attribute and
        // uses this component's directory (INSTALLDIR).
        sb.AppendLine("""            <RemoveFolder Id="RF.InstallDir" On="uninstall" />""");
        sb.AppendLine("""          </Component>""");

        // Bundle the TightVNC MSI under INSTALLDIR\vnc; the agent installs it on first run (EnsureInstalledAsync
        // looks exactly here), so a fresh install brings up VNC without depending on the separate "vnc" rollout.
        if (hasVnc)
        {
            sb.AppendLine("""          <Directory Id="VNCDIR" Name="vnc">""");
            sb.AppendLine($"""            <Component Id="C.Vnc" Guid="{CompVnc}" Win64="yes">""");
            sb.AppendLine($"""              <File Id="F.Vnc" Name="tightvnc.msi" Source="{X(vncMsi!)}" KeyPath="yes" />""");
            sb.AppendLine("""              <RemoveFolder Id="RF.VncDir" On="uninstall" />""");
            sb.AppendLine("""            </Component>""");
            sb.AppendLine("""          </Directory>""");
        }

        sb.AppendLine("""        </Directory>""");
        sb.AppendLine("""      </Directory>""");

        // Optional Start menu shortcut for the console client (perMachine -> All Users Start menu).
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
        if (hasVnc) sb.AppendLine("""      <ComponentRef Id="C.Vnc" />""");
        if (shortcut) sb.AppendLine("""      <ComponentRef Id="C.Shortcut" />""");
        sb.AppendLine("""    </Feature>""");

        // Add/Remove Programs icon, when embedded app.ico exists. Shortcut uses it too.
        if (hasIcon)
        {
            sb.AppendLine($"""    <Icon Id="AppIcon.ico" SourceFile="{X(iconPath!)}" />""");
            sb.AppendLine("""    <Property Id="ARPPRODUCTICON" Value="AppIcon.ico" />""");
        }

        // The agent performs service installation (both services + SCM recovery), not declarative MSI.
        // Install: run the freshly installed exe (type 18 FileKey works during install).
        // The agent composes display service names from owner+group args.
        var ownerArg = string.IsNullOrWhiteSpace(ownerName) ? "" : $" --owner &quot;{X(AsciiFold(ownerName!))}&quot;";
        var groupArg = $" --group &quot;{X(AsciiFold(label))}&quot;";
        sb.AppendLine($"""    <CustomAction Id="CA.InstallSvc" FileKey="F.Agent" ExeCommand="install-service{ownerArg}{groupArg}" Execute="deferred" Impersonate="no" Return="check" />""");
        // Full teardown on uninstall runs the agent's "uninstall-service", which stops the Updater (Helper) FIRST
        // then the Agent — the Helper's watchdog would otherwise restart the Agent mid-uninstall — and also removes
        // TightVNC (service + files + registry). It runs BEFORE StopServices so the supervisor is gone before the
        // agent. ServiceControl is the backstop. We route through "uninstall-service" (not "remove-vnc") because
        // older bundled agents already know that verb and exit cleanly; an unknown verb would launch the host and
        // hang the uninstall. The exe path is captured into a property (CustomActionData) by the immediate action.
        // Best-effort (Return="ignore"). Skipped during major upgrades (UPGRADINGPRODUCTCODE) so nothing is churned.
        sb.AppendLine("""    <CustomAction Id="CA.SetUninst" Property="CA.Uninst" Value="&quot;[#F.Agent]&quot;" Execute="immediate" />""");
        sb.AppendLine("""    <CustomAction Id="CA.Uninst" Property="CA.Uninst" ExeCommand="uninstall-service" Execute="deferred" Impersonate="no" Return="ignore" />""");
        sb.AppendLine("""    <InstallExecuteSequence>""");
        // wixl places MajorUpgrade RemoveExistingProducts before InstallInitialize (1401), which
        // causes "transaction not started" (2762) during upgrade. Move it after.
        sb.AppendLine("""      <RemoveExistingProducts After="InstallInitialize" />""");
        sb.AppendLine("""      <Custom Action="CA.InstallSvc" After="InstallFiles">NOT Installed</Custom>""");
        sb.AppendLine("""      <Custom Action="CA.SetUninst" Before="CA.Uninst">REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE</Custom>""");
        sb.AppendLine("""      <Custom Action="CA.Uninst" Before="StopServices">REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE</Custom>""");
        sb.AppendLine("""    </InstallExecuteSequence>""");

        sb.AppendLine("""  </Product>""");
        sb.AppendLine("""</Wix>""");
        return sb.ToString();
    }

    /// <summary>Optional Authenticode signing with osslsigncode when cert and tool are available.</summary>
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
                logger.LogInformation(L.MsiBuilder_MSISigned);
            }
            else
            {
                logger.LogWarning(L.MsiBuilder_MSISigningSkippedFailedOsslsigncode, code, output);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, L.MsiBuilder_MSISigningErrorSkipped); }
    }

    /// <summary>Removes accents/non-ASCII characters for the Windows-compatible MSI string pool.</summary>
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
        // MSI ProductVersion: max 3-4 numeric parts. Trim non-digit/dot suffixes.
        var clean = new string(v.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "1.0.0" : string.Join('.', parts.Take(4));
    }

    /// <summary>Accent-folded file-name token: keeps letters/digits, turns everything else into single underscores.</summary>
    private static string FileToken(string s)
    {
        var folded = AsciiFold(s);
        var sb = new StringBuilder(folded.Length);
        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().Trim('_');
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? s;
}
