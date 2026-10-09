using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RemoteClient;

/// <summary>
/// Tells whether the other end of a named pipe is the agent service. A pipe name is first come, first served:
/// while the agent is down (restart, update) any signed-in user could create "RemoteAgent.broker" and the console
/// would hand it the operator's sign-in. What only the service can show is that it runs in session 0: a user
/// cannot put a process there without being an administrator, and that is read from the pipe handle with no
/// privilege at all. The image name is checked on top whenever the process can be inspected; a service's cannot
/// be by a standard user (access denied), which is itself consistent with a service. Off Windows there are no
/// such pipes.
/// </summary>
public static class PipePeer
{
    public const string AgentImageName = "RemoteAgent.exe";

    /// <summary>True when the server side of <paramref name="pipe"/> is the agent service; otherwise
    /// <paramref name="why"/> says what was found instead.</summary>
    public static bool IsAgentService(PipeStream pipe, out string why)
    {
        why = "";
        if (!OperatingSystem.IsWindows()) return true;
        return CheckWindows(pipe.SafePipeHandle, out why);
    }

    [SupportedOSPlatform("windows")]
    private static bool CheckWindows(SafePipeHandle handle, out string why)
    {
        if (!GetNamedPipeServerSessionId(handle, out var session)) { why = "pipe server session unknown"; return false; }
        if (session != 0) { why = $"pipe server runs in session {session}, not as a service"; return false; }

        if (!GetNamedPipeServerProcessId(handle, out var pid)) { why = "pipe server process unknown"; return false; }
        var image = ImagePath(pid); // null for a service when we are not an administrator: that is the expected case
        if (image is not null && !string.Equals(Path.GetFileName(image), AgentImageName, StringComparison.OrdinalIgnoreCase))
        {
            why = $"pipe server is {image}, not the agent";
            return false;
        }
        why = "";
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string? ImagePath(uint pid)
    {
        const uint ProcessQueryLimitedInformation = 0x1000;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new char[1024];
            uint size = (uint)buf.Length;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
