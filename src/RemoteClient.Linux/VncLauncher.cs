using System.Diagnostics;
using RemoteAgent.Vnc;

namespace RemoteClient.Linux;

/// <summary>
/// Launches an external TigerVNC viewer against a local forwarded port. The device's VNC password
/// (plaintext, from the server) is handed over via a temporary vncpasswd-format file: TigerVNC's
/// <c>-passwd</c> reads the 8-byte fixed-key-DES obscured form, which <see cref="VncPassword.Encrypt"/>
/// produces (the same format TightVNC uses). The file is created owner-only (0600) in a private folder and
/// removed when the viewer exits.
/// </summary>
internal static class VncLauncher
{
    public static void Launch(int localPort, string vncSecretPlaintext, string scale = "auto", bool color256 = true)
    {
        // Owner-only from the first byte: a file created world-readable and tightened afterwards is open for a
        // moment, and /tmp is shared. The folder is the operator's own (0700), so the name is not even listable.
        var dir = Path.Combine(Path.GetTempPath(), "rac-vnc-" + Environment.UserName);
        Directory.CreateDirectory(dir);
        TryChmod(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var passwdFile = Path.Combine(dir, "passwd-" + Guid.NewGuid().ToString("N"));
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var f = new FileStream(passwdFile, options)) f.Write(VncPassword.Encrypt(vncSecretPlaintext));

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

    private static void TryChmod(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, mode); } catch { /* best effort */ }
    }
}
