using System.Runtime.InteropServices;

namespace RemoteAgent.Consent;

/// <summary>
/// A gépnél ülő felhasználó hozzájárulását kéri egy SYSTEM service-ből: a WTS API-val (WTSSendMessageW)
/// natív Igen/Nem ablakot mutat az AKTÍV konzol-session asztalán, időtúllépéssel. Nem kell hozzá
/// külön folyamat vagy futó kliens. (Ha senki nincs bejelentkezve, azt a hívó az unattended-policyval kezeli.)
/// </summary>
public static class ConsentPrompt
{
    public enum Outcome { NoUser, Granted, Denied, Timeout, Error }

    /// <summary>Van-e épp bejelentkezett interaktív felhasználó az aktív konzol-sessionben.</summary>
    public static bool HasActiveUser()
    {
        uint s = WTSGetActiveConsoleSessionId();
        return s != 0xFFFFFFFF && !string.IsNullOrEmpty(UserNameOf(s));
    }

    /// <summary>Az aktív konzol-session bejelentkezett felhasználója (DOMAIN\\user), vagy null.</summary>
    public static string? ActiveUserName()
    {
        uint s = WTSGetActiveConsoleSessionId();
        if (s == 0xFFFFFFFF) return null;
        var user = UserNameOf(s);
        if (string.IsNullOrEmpty(user)) return null;
        var dom = DomainOf(s);
        return string.IsNullOrEmpty(dom) ? user : $"{dom}\\{user}";
    }

    private static string? DomainOf(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, WTS_INFO_CLASS.WTSDomainName, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    /// <summary>Hozzájárulás-kérés Igen/Nem ablakkal az aktív sessionben; timeoutSeconds után Timeout.</summary>
    public static Outcome Ask(string title, string message, int timeoutSeconds)
    {
        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || string.IsNullOrEmpty(UserNameOf(session)))
            return Outcome.NoUser;

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

    private static string? UserNameOf(uint session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, WTS_INFO_CLASS.WTSUserName, out IntPtr buf, out _))
            return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    private enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7 }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

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
