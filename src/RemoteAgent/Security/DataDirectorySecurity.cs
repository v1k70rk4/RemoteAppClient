using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace RemoteAgent.Security;

/// <summary>
/// The SYSTEM services' data folders under ProgramData (<c>RemoteAgent</c>, and TightVNC's log folder).
///
/// ProgramData lets every user create files and subfolders inside a folder made there with a plain
/// CreateDirectory, and makes them the owner of what they create - no place for files that SYSTEM services
/// read and act on (update markers, the staged executable, the logging switch) or keep secrets in (the device
/// key, the VNC password). <see cref="Secure"/> gives such a folder a protected ACL - SYSTEM and Administrators
/// only; users may at most open the folder itself and read the files named in <see cref="UserReadableFiles"/> -
/// and removes whatever someone else put inside: anything not owned by SYSTEM or Administrators, and every
/// link (a junction or symlink could otherwise steer a SYSTEM write elsewhere).
///
/// Linked into RemoteAgent.Updater as well: the Helper secures the folder when it starts, too.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DataDirectorySecurity
{
    public static string AgentDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteAgent");

    public static string TightVncDataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TightVNC");

    /// <summary>Files the console on the same machine reads as the signed-in user (the device id, the server).</summary>
    public static readonly IReadOnlyList<string> UserReadableFiles = ["enrollment.json"];

    /// <summary>
    /// The enrollment's own files. An administrator running <c>enroll</c> by hand may own them personally (a
    /// policy can make the creator, not the Administrators group, the owner), so in the agent folder these are
    /// taken back and locked down rather than deleted - deleting them would unenroll the device.
    /// </summary>
    private static readonly HashSet<string> EnrollmentFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "enrollment.json", "agent.pfx.dat", "ca.crt", "id_ed25519", "id_ed25519.pub", "id_ed25519-cert.pub", "vnc.secret",
    };

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <param name="Applied">The ACL is in place.</param>
    /// <param name="Removed">Items someone else had put in the folder, now deleted.</param>
    /// <param name="Error">Why it could not be applied; null when <paramref name="Applied"/>.</param>
    public sealed record Result(bool Applied, int Removed, string? Error);

    /// <summary>
    /// Creates <paramref name="directory"/> if needed and secures it. Only the two known folders are touched -
    /// a misconfigured path never gets its ACL rewritten. Never throws.
    /// </summary>
    public static Result Secure(string directory)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); }
        catch (Exception ex) { return new Result(false, 0, ex.Message); }
        if (!string.Equals(full, AgentDataDirectory, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, TightVncDataDirectory, StringComparison.OrdinalIgnoreCase))
            return new Result(false, 0, "not a known data directory: " + full);

        try
        {
            // Lets SYSTEM take back an item whose owner locked it out. Best effort: Administrators-owned
            // items and our own folder need neither privilege.
            Privileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege");

            var dir = new DirectoryInfo(full);
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                dir.Delete();   // a link put in place of the folder: remove the link itself, never follow it

            Directory.CreateDirectory(full);
            dir = new DirectoryInfo(full);
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))   // put back in the moment between: leave it alone
                return new Result(false, 0, "the folder is a link: " + full);
            TakeOwnership(dir);
            dir.SetAccessControl(FolderSecurity());

            bool isAgentFolder = string.Equals(full, AgentDataDirectory, StringComparison.OrdinalIgnoreCase);
            int removed = Sweep(dir, keepEnrollmentFiles: isAgentFolder);
            foreach (var name in UserReadableFiles)
            {
                var file = new FileInfo(Path.Combine(full, name));
                if (file.Exists) AllowUsersToRead(file);
            }
            return new Result(true, removed, null);
        }
        catch (Exception ex)
        {
            return new Result(false, 0, ex.Message);
        }
    }

    /// <summary>True when <paramref name="path"/> is owned by SYSTEM or Administrators - who alone may create
    /// files in a secured folder. A file anyone else owns was not put there by the agent or the Helper.</summary>
    public static bool IsOwnedBySystemOrAdministrators(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            return owner is SecurityIdentifier sid && (sid.Equals(LocalSystem) || sid.Equals(Administrators));
        }
        catch { return false; }
    }

    /// <summary>
    /// Hands a file this process wrote to Administrators, inheriting the folder's ACL. For files an
    /// administrator writes by hand (<c>bootstrap</c>), which a creator-owner policy would otherwise leave
    /// personally owned - and the next <see cref="Secure"/> would treat as someone else's.
    /// </summary>
    public static void ClaimForAdministrators(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            TakeOwnership(file);
            ResetToInherited(file);
        }
        catch { /* best effort: the file still works, the next Secure may remove it */ }
    }

    /// <summary>
    /// Opens a file for writing that this process has just created: an existing file of that name is deleted
    /// first, so a file someone else created - and still holds rights to - is never written into and used.
    /// </summary>
    public static FileStream CreateNew(string path)
    {
        File.Delete(path);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <summary>
    /// The executable a service runs, from its ImagePath in the registry (writable by administrators only):
    /// quotes and arguments removed, environment variables expanded. Null when it cannot be read.
    /// </summary>
    public static string? ServiceExecutablePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;
            var path = Environment.ExpandEnvironmentVariables(raw).Trim();
            if (path.StartsWith('"'))
            {
                var end = path.IndexOf('"', 1);
                path = end > 1 ? path[1..end] : path.Trim('"');
            }
            else
            {
                var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) path = path[..(exe + 4)];
            }
            return Path.GetFullPath(path);
        }
        catch { return null; }
    }

    // SYSTEM and Administrators: full control, inherited by everything below. Users: may open this folder
    // itself (to reach the files that carry their own read grant), nothing inherited. Inheritance from
    // ProgramData is cut - that is where the users' create rights came from.
    private static DirectorySecurity FolderSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    // Walks the folder without following links. Items owned by SYSTEM or Administrators stay (a trusted folder
    // is walked further, since someone may have dropped files into it while it was open). The enrollment's own
    // files, directly in the agent folder, are taken back and locked down whoever owns them. Everything else -
    // and every link - is taken back and deleted.
    private static int Sweep(DirectoryInfo dir, bool keepEnrollmentFiles)
    {
        int removed = 0;
        foreach (var item in dir.EnumerateFileSystemInfos())
        {
            bool isLink = item.Attributes.HasFlag(FileAttributes.ReparsePoint);
            if (!isLink && IsOwnedBySystemOrAdministrators(item.FullName))
            {
                if (item is DirectoryInfo sub) removed += Sweep(sub, keepEnrollmentFiles: false);
                continue;
            }
            if (!isLink && keepEnrollmentFiles && item is FileInfo && EnrollmentFiles.Contains(item.Name))
            {
                TakeOwnership(item);
                ResetToInherited(item);
                continue;
            }
            Remove(item);
            removed++;
        }
        return removed;
    }

    // A link is deleted as a link and nothing else: setting an ACL or an owner on it would act on whatever it
    // points at (C:\Windows, say). Anything else is taken back - ownership, then its own ACEs dropped so it
    // inherits SYSTEM/Administrators from the secured parent - and deleted; a foreign folder is emptied the same
    // way first.
    private static void Remove(FileSystemInfo item)
    {
        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            item.Delete();   // DirectoryInfo.Delete / FileInfo.Delete remove the link itself, never the target
            return;
        }
        TakeOwnership(item);
        ResetToInherited(item);
        if (item is DirectoryInfo d)
        {
            foreach (var child in d.EnumerateFileSystemInfos()) Remove(child);
            d.Delete();
        }
        else
        {
            if (item.Attributes.HasFlag(FileAttributes.ReadOnly)) item.Attributes &= ~FileAttributes.ReadOnly;
            item.Delete();
        }
    }

    private static void TakeOwnership(FileSystemInfo item)
    {
        if (item is DirectoryInfo d)
        {
            var sec = new DirectorySecurity();
            sec.SetOwner(Administrators);
            d.SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetOwner(Administrators);
            ((FileInfo)item).SetAccessControl(sec);
        }
    }

    private static void ResetToInherited(FileSystemInfo item)
    {
        if (item is DirectoryInfo d)
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            d.SetAccessControl(sec);
        }
        else
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            ((FileInfo)item).SetAccessControl(sec);
        }
    }

    private static void AllowUsersToRead(FileInfo file)
    {
        var sec = file.GetAccessControl(AccessControlSections.Access);
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(sec);
    }

    private static class Privileges
    {
        private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 0x2;

        public static void Enable(params string[] names)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return;
            try
            {
                foreach (var name in names)
                {
                    if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                    var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                    AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);   // fails quietly when not held
                }
            }
            finally { CloseHandle(token); }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivileges { public int Count; public long Luid; public uint Attributes; }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
    }
}
