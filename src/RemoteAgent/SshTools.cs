namespace RemoteAgent;

/// <summary>
/// Locates the OpenSSH client executables (ssh.exe / ssh-keygen.exe). Windows 10 / Server 2019+ ship them
/// in %SystemRoot%\System32\OpenSSH; older systems (e.g. Windows Server 2016) or manual installs put them
/// under %ProgramFiles%\OpenSSH. A configured override (AgentOptions.SshExecutablePath) wins when it points
/// at an existing file; otherwise we probe the known locations and finally fall back to PATH.
/// </summary>
public static class SshTools
{
    /// <summary>Path to ssh.exe. <paramref name="configuredPath"/> is the optional AgentOptions override.</summary>
    public static string ResolveSsh(string? configuredPath = null) => Resolve("ssh.exe", configuredPath);

    /// <summary>Path to ssh-keygen.exe.</summary>
    public static string ResolveSshKeygen() => Resolve("ssh-keygen.exe", null);

    private static string Resolve(string exe, string? configuredPath)
    {
        // 1) explicit config, if it actually exists
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

        // 2) known install locations
        foreach (var dir in CandidateDirs())
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }

        // 3) let CreateProcess resolve it from PATH
        return exe;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        yield return Path.Combine(Environment.SystemDirectory, "OpenSSH"); // System32\OpenSSH (Win10/Server2019+)
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "OpenSSH"); // Program Files\OpenSSH (manual install)
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pfx86)) yield return Path.Combine(pfx86, "OpenSSH");
    }
}
