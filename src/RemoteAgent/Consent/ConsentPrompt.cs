using System.Runtime.InteropServices;

namespace RemoteAgent.Consent;

/// <summary>
/// Requests consent from the user sitting at the device from a SYSTEM service. Uses WTS
/// (WTSSendMessageW) to show a native Yes/No dialog on the user's active session desktop,
/// with timeout. No helper process or running client is required. If nobody is signed in,
/// the caller handles that through unattended policy.
///
/// User presence and the prompt target are resolved by enumerating sessions and picking the
/// active one that has a signed-in user. This covers both the physical console and RDP, where the
/// console session sits empty at the logon screen while the real user works in an RDP session —
/// the old console-only check there reported "no user" and silently skipped consent.
/// </summary>
public static class ConsentPrompt
{
    public enum Outcome { NoUser, Granted, Denied, Timeout, Error }

    private const uint NoSession = 0xFFFFFFFF;

    /// <summary>Whether an interactive user is currently signed in to an active session (console or RDP).</summary>
    public static bool HasActiveUser() => ResolveActiveSession() != NoSession;

    /// <summary>Signed-in user of the active session (DOMAIN\\user), or null.</summary>
    public static string? ActiveUserName()
    {
        uint s = ResolveActiveSession();
        if (s == NoSession) return null;
        var user = UserNameOf(s);
        if (string.IsNullOrEmpty(user)) return null;
        var dom = DomainOf(s);
        return string.IsNullOrEmpty(dom) ? user : $"{dom}\\{user}";
    }

    /// <summary>Requests consent with a Yes/No dialog in the active session; returns Timeout after timeoutSeconds.</summary>
    public static Outcome Ask(string title, string message, int timeoutSeconds)
    {
        uint session = ResolveActiveSession();
        if (session == NoSession) return Outcome.NoUser;

        const uint MB_YESNO = 0x4, MB_ICONQUESTION = 0x20, MB_SYSTEMMODAL = 0x1000,
                   MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;
        const int IDYES = 6, IDTIMEOUT = 32000;

        bool ok = WTSSendMessage(
            IntPtr.Zero, session,
            title, title.Length * 2,
            message, message.Length * 2,
            MB_YESNO | MB_ICONQUESTION | MB_SYSTEMMODAL | MB_TOPMOST | MB_SETFOREGROUND,
            (uint)Math.Max(0, timeoutSeconds), out int response, bWait: true);

        if (!ok) return Outcome.Error;
        return response switch { IDYES => Outcome.Granted, IDTIMEOUT => Outcome.Timeout, _ => Outcome.Denied };
    }

    /// <summary>
    /// Shows a plain message + OK in the active session and returns immediately (does not wait for the
    /// user to click). Returns false when nobody is signed in. Used for the operator message.
    /// </summary>
    public static bool Notify(string title, string message)
    {
        uint session = ResolveActiveSession();
        if (session == NoSession) return false;

        const uint MB_OK = 0x0, MB_ICONINFORMATION = 0x40, MB_TOPMOST = 0x40000, MB_SETFOREGROUND = 0x10000;

        WTSSendMessage(
            IntPtr.Zero, session,
            title, title.Length * 2,
            message, message.Length * 2,
            MB_OK | MB_ICONINFORMATION | MB_TOPMOST | MB_SETFOREGROUND,
            0, out _, bWait: false); // fire-and-forget; we do not block on the user's OK
        return true;
    }

    /// <summary>
    /// The session to prompt: the active (foreground) session that has a signed-in user. Enumerates all
    /// sessions so RDP and fast-user-switching are handled, not just the physical console. Falls back to
    /// the console session. Returns <see cref="NoSession"/> when nobody is signed in anywhere active.
    /// </summary>
    private static uint ResolveActiveSession()
    {
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr list, out int count) && list != IntPtr.Zero)
        {
            try
            {
                int size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (int i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(list + i * size);
                    if (info.State == WTSActive && !string.IsNullOrEmpty(UserNameOf(info.SessionId)))
                        return info.SessionId;
                }
            }
            catch { /* fall back to the console session */ }
            finally { WTSFreeMemory(list); }
        }

        uint console = WTSGetActiveConsoleSessionId();
        return console != NoSession && !string.IsNullOrEmpty(UserNameOf(console)) ? console : NoSession;
    }

    private static string? DomainOf(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, WTS_INFO_CLASS.WTSDomainName, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private static string? UserNameOf(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, WTS_INFO_CLASS.WTSUserName, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7 }

    private const int WTSActive = 0; // WTS_CONNECTSTATE_CLASS.WTSActive

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State; // WTS_CONNECTSTATE_CLASS
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessage(
        IntPtr hServer, uint sessionId,
        string pTitle, int titleLength,
        string pMessage, int messageLength,
        uint style, uint timeout, out int pResponse, bool bWait);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
