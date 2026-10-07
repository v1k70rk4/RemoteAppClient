using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using L = RemoteAgent.Localization.Strings;

namespace RemoteAgent.Power;

/// <summary>
/// Holds a Windows power request ("system required") while a remote session is open, so a device whose
/// power plan sleeps it after a few idle minutes does not doze off under the operator. It is the same
/// mechanism that keeps a laptop awake while it plays music; <c>powercfg /requests</c> lists it under
/// SYSTEM with our reason. Released when the tunnel closes (idle timeout, close command, shutdown), so an
/// unattended device sleeps exactly as its policy says. It cannot wake a sleeping device, only keep an
/// awake one awake: the operator still has to catch it up, the session then lasts as long as it needs to.
/// </summary>
public sealed class SessionKeepAwake(ILogger<SessionKeepAwake> logger) : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;   // the union's LPWSTR member; POWER_REQUEST_CONTEXT_SIMPLE_STRING selects it
    }

    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;           // POWER_REQUEST_TYPE
    private static readonly IntPtr InvalidHandle = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    private readonly object _gate = new();
    private IntPtr _request;   // valid while held
    private IntPtr _reason;    // the unmanaged reason string, kept for the request's lifetime

    public bool Held { get { lock (_gate) return _request != IntPtr.Zero; } }

    /// <summary>Keeps the system awake from now until <see cref="Release"/>. Idempotent; a failure is logged
    /// and the session goes on without it, possibly shorter.</summary>
    public void Hold()
    {
        lock (_gate)
        {
            if (_request != IntPtr.Zero) return;
            try
            {
                _reason = Marshal.StringToHGlobalUni(L.SessionKeepAwake_Reason);
                var ctx = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = _reason };
                var h = PowerCreateRequest(ref ctx);
                if (h == IntPtr.Zero || h == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!PowerSetRequest(h, PowerRequestSystemRequired))
                {
                    var err = Marshal.GetLastWin32Error();
                    CloseHandle(h);
                    throw new Win32Exception(err);
                }
                _request = h;
                logger.LogInformation(L.SessionKeepAwake_Held);
            }
            catch (Exception ex)
            {
                FreeReason();
                logger.LogWarning(ex, L.SessionKeepAwake_Failed);
            }
        }
    }

    /// <summary>Lets the system sleep again. Idempotent.</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (_request == IntPtr.Zero) return;
            try { PowerClearRequest(_request, PowerRequestSystemRequired); CloseHandle(_request); }
            catch { /* the handle dies with the process anyway */ }
            _request = IntPtr.Zero;
            FreeReason();
            logger.LogInformation(L.SessionKeepAwake_Released);
        }
    }

    private void FreeReason()
    {
        if (_reason == IntPtr.Zero) return;
        Marshal.FreeHGlobal(_reason);
        _reason = IntPtr.Zero;
    }

    public void Dispose() => Release();
}
