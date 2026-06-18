using System.Diagnostics;
using RemoteAgent.Vnc;

namespace RemoteClient.Linux;

/// <summary>
/// Launches an external TigerVNC viewer against a local forwarded port. The device's VNC password
/// (plaintext, from the server) is handed over via a temporary vncpasswd-format file: TigerVNC's
/// <c>-passwd</c> reads the 8-byte fixed-key-DES obscured form, which <see cref="VncPassword.Encrypt"/>
/// produces (the same format TightVNC uses). The file is chmod 600 and removed when the viewer exits.
/// </summary>
internal static class VncLauncher
{
    public static void Launch(int localPort, string vncSecretPlaintext, string scale = "auto", bool color256 = true)
    {
        var passwdFile = Path.Combine(Path.GetTempPath(), "rac_vnc_" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(passwdFile, VncPassword.Encrypt(vncSecretPlaintext));
        TryChmod600(passwdFile);

        // Prefer ssvnc's "Enhanced TightVNC Viewer" (native): it has client-side scaling (-scale fit =
        // fit-to-window) and 256-color (-bgr233). Fall back to TigerVNC (no client scaling) if absent.
        ProcessStartInfo psi;
        if (Which("ssvncviewer") is { } ssvnc)
        {
            psi = new ProcessStartInfo(ssvnc) { UseShellExecute = false };
            psi.ArgumentList.Add("-passwd"); psi.ArgumentList.Add(passwdFile);
            // ssvncviewer's "-scale fit" renders tiny, so use a numeric scale (configurable). The operator
            // can still adjust live with the s / + / - / 1-6 keys in the viewer.
            if (!string.IsNullOrWhiteSpace(scale) && !scale.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                psi.ArgumentList.Add("-scale"); psi.ArgumentList.Add(scale);
            }
            if (color256) { psi.ArgumentList.Add("-bgr233"); }           // 256-color, low bandwidth (configurable)
            psi.ArgumentList.Add($"127.0.0.1::{localPort}");
        }
        else
        {
            var viewer = Which("vncviewer") ?? Which("xtigervncviewer")
                ?? throw new InvalidOperationException("No VNC viewer found - install 'ssvnc' (preferred) or 'tigervnc-viewer'.");
            psi = new ProcessStartInfo(viewer) { UseShellExecute = false };
            psi.ArgumentList.Add("-passwd"); psi.ArgumentList.Add(passwdFile);
            psi.ArgumentList.Add($"127.0.0.1::{localPort}"); // "::" = exact port; TigerVNC has no client scaling
        }

        var proc = Process.Start(psi);

        // Remove the temp password file once the viewer is done with it (best effort).
        _ = Task.Run(async () =>
        {
            try { if (proc is not null) await proc.WaitForExitAsync(); else await Task.Delay(8000); } catch { /* ignore */ }
            try { File.Delete(passwdFile); } catch { /* ignore */ }
        });
    }

    private static string? Which(string cmd)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.Combine(dir, cmd);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static void TryChmod600(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
    }
}
