using System.Diagnostics;
using System.Runtime.InteropServices;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Power;

/// <summary>
/// Power actions executed by the SYSTEM service. Restart/cancel use shutdown.exe (system-wide).
/// User logout uses WTSLogoffSession on the active console session — "shutdown /l" would target
/// session 0 (the service), not the interactive user. Only fixed, server-vetted actions run here;
/// no shell string travels the wire.
/// </summary>
public static class PowerControl
{
    private const int GraceSeconds = 60;

    /// <summary>Schedules a restart in 60s with a user-visible comment. force = also force-close apps.</summary>
    public static bool Restart(bool force)
    {
        var args = new List<string> { "/r", "/t", GraceSeconds.ToString() };
        if (force) args.Add("/f");
        args.Add("/c"); args.Add(L.PowerControl_RestartComment);
        return RunShutdown(args);
    }

    /// <summary>Cancels a pending shutdown/restart (best effort; non-zero exit when nothing is pending).</summary>
    public static bool Cancel() => RunShutdown(["/a"]);

    /// <summary>Logs off the interactive user on the active console session. False if nobody is signed in.</summary>
    public static bool LogoffActiveUser()
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return false;
        return WTSLogoffSession(IntPtr.Zero, session, bWait: false);
    }

    private static bool RunShutdown(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("shutdown.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool bWait);
}
