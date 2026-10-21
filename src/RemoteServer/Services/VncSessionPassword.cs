using System.Security.Cryptography;

namespace RemoteServer.Services;

/// <summary>
/// The VNC password of a session. Until now a device's VNC password stood for as long as the agent kept it and
/// went out to every console user in the device list, so an operator whose access ended could still connect
/// while someone else had the device's tunnel open. Now the server chooses the password at open-tunnel: it goes
/// to the agent in the signed command (set on the VNC server before the tunnel comes up) and to the operator in
/// the result, and the list no longer carries it. One password is kept for <see cref="Window"/> from the time it
/// was chosen, so repeated opens within the hour - a second operator joining, a reconnect - reuse it and the
/// agent's VNC server is not restarted each time.
/// </summary>
public static class VncSessionPassword
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    // Classic VncAuth keys on the first 8 bytes; no look-alike characters (0/O, 1/l/I) for the operator who reads it.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A fresh 8-character password.</summary>
    public static string Generate()
    {
        Span<char> chars = stackalloc char[8];
        for (int i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>Whether the password chosen at <paramref name="chosenAt"/> is still the session's at
    /// <paramref name="now"/>.</summary>
    public static bool Reusable(DateTimeOffset? chosenAt, DateTimeOffset now) =>
        chosenAt is { } t && now - t < Window && t <= now;
}
