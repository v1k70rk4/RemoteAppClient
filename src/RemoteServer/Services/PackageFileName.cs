namespace RemoteServer.Services;

/// <summary>
/// The file name an uploaded update package is stored under. It carries the package's own hash, so two uploads
/// of the same component and version with different bytes land in different files: a record that points at a
/// file keeps pointing at the bytes it was hashed from, however many times the version is uploaded again, and
/// records on several channels may share one file safely. Before this, a re-upload overwrote the file in place
/// and every other record on it (a promoted channel's, typically) kept promising the old hash, so devices
/// discarded what they downloaded.
/// </summary>
public static class PackageFileName
{
    public const int HashChars = 12;

    public static string Extension(string component) => component == "vnc" ? "msi" : "exe";

    public static string For(string component, string channel, string version, string sha256)
    {
        if (sha256.Length < HashChars) throw new ArgumentException("not a SHA-256 hex digest", nameof(sha256));
        var safeVer = new string(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray());
        return $"{component}-{channel}-{safeVer}-{sha256[..HashChars].ToLowerInvariant()}.{Extension(component)}";
    }
}
