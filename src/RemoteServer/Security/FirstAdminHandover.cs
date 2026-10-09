using L = RemoteServer.Localization.Strings;

namespace RemoteServer.Security;

/// <summary>
/// The first admin's temporary password, handed over through an owner-only file instead of the server log.
/// Written once when the database is seeded, removed as soon as that password has been changed.
/// </summary>
public static class FirstAdminHandover
{
    /// <summary>Writes "admin / password" to <paramref name="path"/> readable by the service user only. False when
    /// the file could not be written, so the caller can fall back.</summary>
    public static bool Write(string path, string password, ILogger log)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var f = new StreamWriter(new FileStream(path, options)))
            {
                f.WriteLine("username: admin");
                f.WriteLine($"temporary password: {password}");
                f.WriteLine("(must be changed at first sign-in; this file is removed once it has been)");
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // also when the file pre-existed
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, L.FirstAdminHandover_CouldNotWrite, path);
            return false;
        }
    }

    /// <summary>Removes the file once the temporary password is no longer valid. Best effort.</summary>
    public static void Forget(string path, ILogger log)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { log.LogWarning(ex, L.FirstAdminHandover_CouldNotRemove, path); }
    }
}
