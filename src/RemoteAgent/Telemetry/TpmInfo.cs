using System.Diagnostics;
using System.Text;

namespace RemoteAgent.Telemetry;

/// <summary>
/// The device's TPM as <c>tpmtool getdeviceinformation</c> reports it (Windows 10 1809 and later). Its output is
/// English on every display language, lines of "-Key: Value". No WMI, like the rest of the collector.
///
/// The state hardly ever changes, and tpmtool takes a second or two, so it is read once and then every six
/// hours - an earlier result is reused meanwhile. Unknown (all null) where tpmtool is missing or fails.
/// </summary>
public static class TpmInfo
{
    public sealed record State(bool? Present, string? Version, string? Manufacturer, bool? Ready, bool? Attestation, bool? VulnerableFirmware)
    {
        public static readonly State Unknown = new(null, null, null, null, null, null);
    }

    private static readonly TimeSpan Refresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(10);
    private static readonly object Gate = new();
    private static State _last = State.Unknown;
    private static DateTimeOffset _next = DateTimeOffset.MinValue;

    public static State Read()
    {
        lock (Gate)
        {
            if (DateTimeOffset.UtcNow < _next) return _last;
            var now = Query();
            // A failed or unreadable probe (tpmtool hung, odd output) keeps the last good answer rather than
            // blanking the console, and is tried again soon instead of in six hours.
            bool known = now is not null && now != State.Unknown;
            if (known) _last = now!;
            _next = DateTimeOffset.UtcNow + (known ? Refresh : Retry);
            return _last;
        }
    }

    private static State? Query()
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "tpmtool.exe");
            if (!File.Exists(exe)) return State.Unknown;
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("getdeviceinformation");
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return null; }
            return Parse(output.Result);
        }
        catch { return null; }
    }

    /// <summary>Parses tpmtool's "-Key: Value" lines. Public for the tests.</summary>
    public static State Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('-')) continue;
            int i = line.IndexOf(':');
            if (i < 2) continue;
            values.TryAdd(line[1..i].Trim(), line[(i + 1)..].Trim());
        }

        bool? Flag(string key) => values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;
        string? Text(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

        var present = Flag("TPM Present");
        if (present is false) return new State(false, null, null, false, false, null);
        return new State(present, Text("TPM Version"), Text("TPM Manufacturer ID"),
            Flag("Ready For Storage"), Flag("Ready For Attestation"), Flag("TPM Has Vulnerable Firmware"));
    }
}
