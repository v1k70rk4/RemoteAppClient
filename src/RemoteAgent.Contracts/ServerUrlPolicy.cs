namespace RemoteAgent.Admin;

/// <summary>
/// Which server addresses an agent or console may talk to: HTTPS, or plain HTTP only on the local machine
/// (development). A plain-HTTP server elsewhere would carry the device's VNC password, the telemetry and the
/// operator's sign-in in the clear to whoever sits on the path.
/// </summary>
public static class ServerUrlPolicy
{
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is "https" or "wss") return true;
        if (uri.Scheme is "http" or "ws") return uri.IsLoopback;
        return false;
    }
}
