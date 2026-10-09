namespace RemoteClient;

/// <summary>
/// Full paths of the OpenSSH client tools for the consoles that shell out to them (Lite, Linux). Starting
/// "ssh" by bare name would take whatever the PATH offers first, including a same-named program dropped into a
/// user-writable folder ahead of the system one; the operator's key and certificate would then go to it. So the
/// system location is tried first and a PATH entry only after, and the result is always an absolute path.
/// </summary>
public static class SshLocator
{
    public static string Ssh() => Find("ssh");
    public static string SshKeygen() => Find("ssh-keygen");

    private static string Find(string tool)
    {
        if (OperatingSystem.IsWindows())
        {
            var exe = tool + ".exe";
            var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", exe);
            if (File.Exists(system)) return system;
            return OnPath(exe) ?? throw new InvalidOperationException("ssh_not_found");
        }
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/local/bin" })
        {
            var p = Path.Combine(dir, tool);
            if (File.Exists(p)) return p;
        }
        return OnPath(tool) ?? throw new InvalidOperationException("ssh_not_found");
    }

    private static string? OnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathRooted(dir)) continue; // a relative PATH entry means "wherever we happen to be"
            var p = Path.Combine(dir, file);
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }
}
