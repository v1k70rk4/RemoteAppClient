namespace RemoteClient;

/// <summary>
/// File names as they arrive in a listing - for a download, from the managed device - checked before they become
/// part of a path. A plain name is one path segment under Windows rules, whichever system runs this: not "." or
/// "..", no separator, drive colon, wildcard, quote, pipe or control character, and not a Windows device name
/// (CON, NUL, COM1...), which Windows resolves to the device in any folder.
/// </summary>
public static class SafeNames
{
    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    private static readonly HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsPlainName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false; // Windows drops them: "CON " is CON, "..." is "."
        if (name.IndexOfAny(Forbidden) >= 0 || name.Any(char.IsControl)) return false;
        return !Devices.Contains(name.Split('.')[0].TrimEnd(' '));
    }
}
