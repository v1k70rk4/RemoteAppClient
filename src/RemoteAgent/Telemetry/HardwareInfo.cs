using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RemoteAgent.Telemetry;

/// <summary>
/// System manufacturer / model / serial from SMBIOS (firmware) without WMI, so it stays
/// NativeAOT-friendly. Reads the raw SMBIOS table via GetSystemFirmwareTable and parses the
/// Type 1 (System Information) structure; falls back to the registry for make/model. Common OEM
/// placeholder strings ("To Be Filled By O.E.M.", "Default string", …) are treated as unknown, so
/// generic/custom desktops report null instead of junk.
/// </summary>
internal static class HardwareInfo
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    private const uint RSMB = 0x52534D42; // 'RSMB' raw SMBIOS table provider

    public static (string? Manufacturer, string? Model, string? Serial) System()
    {
        try
        {
            if (ParseSmbios() is { } s && (s.Manufacturer is not null || s.Model is not null || s.Serial is not null))
                return s;
        }
        catch { /* fall through to the registry */ }

        // Registry fallback: make/model only (the serial number is not exposed there).
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return (Clean(k?.GetValue("SystemManufacturer") as string),
                    Clean(k?.GetValue("SystemProductName") as string),
                    null);
        }
        catch { return (null, null, null); }
    }

    private static (string? Manufacturer, string? Model, string? Serial)? ParseSmbios()
    {
        uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
        if (size == 0) return null;
        var buf = new byte[size];
        if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0 || buf.Length < 8) return null;

        // RawSMBIOSData: calling-method, major, minor, dmiRevision (4 bytes), uint length, then the table.
        int len = BitConverter.ToInt32(buf, 4);
        int end = Math.Min(8 + len, buf.Length);
        int p = 8;
        while (p + 4 <= end)
        {
            byte type = buf[p];
            byte formattedLen = buf[p + 1];
            if (formattedLen < 4) break;
            int stringsStart = p + formattedLen;
            if (stringsStart > buf.Length) break;

            if (type == 1) // Type 1 = System Information
            {
                var strings = ReadStrings(buf, stringsStart);
                byte miMan = p + 0x04 < stringsStart ? buf[p + 0x04] : (byte)0;
                byte miMod = p + 0x05 < stringsStart ? buf[p + 0x05] : (byte)0;
                byte miSer = p + 0x07 < stringsStart ? buf[p + 0x07] : (byte)0;
                return (Clean(Str(strings, miMan)), Clean(Str(strings, miMod)), Clean(Str(strings, miSer)));
            }

            // Skip the formatted area and the string set (which ends with a double NUL).
            int q = stringsStart;
            while (q + 1 < buf.Length && !(buf[q] == 0 && buf[q + 1] == 0)) q++;
            p = q + 2;
        }
        return null;
    }

    private static List<string> ReadStrings(byte[] buf, int start)
    {
        var list = new List<string>();
        int p = start;
        while (p < buf.Length && buf[p] != 0)
        {
            int s = p;
            while (p < buf.Length && buf[p] != 0) p++;
            list.Add(Encoding.ASCII.GetString(buf, s, p - s));
            p++; // skip the NUL between strings
        }
        return list;
    }

    private static string? Str(List<string> strings, byte index) =>
        index >= 1 && index <= strings.Count ? strings[index - 1] : null;

    // SMBIOS fields are frequently left as these placeholders on custom/desktop builds.
    private static readonly string[] Junk =
    {
        "to be filled by o.e.m.", "to be filled by oem", "default string", "system manufacturer",
        "system product name", "system version", "system serial number", "o.e.m.", "oem", "none",
        "not applicable", "not specified", "default", "invalid", "0", "00000000", "123456789", "sku",
    };

    private static string? Clean(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        foreach (var j in Junk)
            if (string.Equals(s, j, StringComparison.OrdinalIgnoreCase)) return null;
        return s;
    }
}
