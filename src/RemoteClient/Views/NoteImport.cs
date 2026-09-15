using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>
/// Reads a "hostname;note" list and works out what importing it would do, without changing anything. Kept apart
/// from the window so the rules sit in one place:
/// <list type="bullet">
/// <item>One device per line, split at the first ';' or tab. A hostname can contain neither, so a note may.</item>
/// <item>Excel's quoting ("a;b", doubled quotes) and its trailing empty columns are undone.</item>
/// <item>A header line is recognised and ignored.</item>
/// <item>Only devices already in the list are touched; an unknown name is reported, never created.</item>
/// <item>A blank note never clears an existing one.</item>
/// <item>A name several devices share goes to the one seen most recently. The others are usually stale
/// re-enrollments, and leaving them without a note is exactly what makes them easy to find and delete.</item>
/// <item>A device listed twice gets the later line, as in any override list.</item>
/// </list>
/// </summary>
internal static class NoteImport
{
    /// <summary>Declared in the order the preview sorts and sums up by: what gets written, then what deserves a look,
    /// then what is skipped for a harmless reason.</summary>
    public enum Outcome { New, Overwrite, NotFound, Invalid, Repeated, EmptyNote, Unchanged, Header }

    public sealed class Row
    {
        public int Line { get; init; }
        /// <summary>The name as written in the list (the whole line when it has no separator).</summary>
        public string Host { get; init; } = "";
        public string Note { get; init; } = "";
        public Outcome Outcome { get; set; }
        /// <summary>The device this line resolves to; null when nothing matched.</summary>
        public DeviceInfo? Device { get; init; }
        /// <summary>How many listed devices carry this name (above 1 = duplicates; the latest one is <see cref="Device"/>).</summary>
        public int SameName { get; init; }
        /// <summary>The short name that matched when the list gave a fully qualified one; otherwise null.</summary>
        public string? MatchedAs { get; init; }
        /// <summary>For <see cref="Outcome.Repeated"/>: the later line that sets the same device.</summary>
        public int SupersededBy { get; set; }
    }

    public static List<Row> Build(string text, IReadOnlyList<DeviceInfo> devices)
    {
        // The agent reports the NetBIOS name; an inventory list may spell it in any case.
        var byName = new Dictionary<string, List<DeviceInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in devices)
        {
            if (string.IsNullOrWhiteSpace(d.Hostname)) continue;
            var key = d.Hostname.Trim();
            if (!byName.TryGetValue(key, out var same)) byName[key] = same = [];
            same.Add(d);
        }

        var rows = new List<Row>();
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
        bool firstLine = true;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            bool isFirst = firstLine;
            firstLine = false;

            int sep = line.IndexOfAny([';', '\t']);
            if (sep < 0) { rows.Add(new Row { Line = i + 1, Host = line, Outcome = Outcome.Invalid }); continue; }

            var host = Unquote(line[..sep].Trim());
            // Trailing separators first: a sheet that ever had a third column exports "note;;" on every row.
            var note = Unquote(line[(sep + 1)..].TrimEnd(';', '\t', ' ').Trim());
            if (host.Length == 0) { rows.Add(new Row { Line = i + 1, Host = line, Outcome = Outcome.Invalid }); continue; }

            string? matchedAs = null;
            if (!byName.TryGetValue(host, out var matches))
            {
                // A fully qualified name from an inventory export ("PC01.corp.local") still means the NetBIOS name.
                int dot = host.IndexOf('.');
                if (dot > 0 && byName.TryGetValue(host[..dot], out matches)) matchedAs = host[..dot];
            }

            if (matches is null)
            {
                rows.Add(new Row { Line = i + 1, Host = host, Note = note, Outcome = isFirst && LooksLikeHeader(host) ? Outcome.Header : Outcome.NotFound });
                continue;
            }

            var device = matches.OrderByDescending(d => d.LastSeenAt ?? DateTimeOffset.MinValue).First();
            var current = (device.Note ?? "").Trim();
            var outcome = note.Length == 0 ? Outcome.EmptyNote
                : string.Equals(current, note, StringComparison.Ordinal) ? Outcome.Unchanged
                : current.Length == 0 ? Outcome.New
                : Outcome.Overwrite;
            rows.Add(new Row { Line = i + 1, Host = host, Note = note, Outcome = outcome, Device = device, SameName = matches.Count, MatchedAs = matchedAs });
        }

        // Later line wins for the same device. Blank notes take no part: they are not an instruction at all.
        var lastFor = new Dictionary<string, Row>(StringComparer.Ordinal);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            if (r.Device is null || r.Outcome is not (Outcome.New or Outcome.Overwrite or Outcome.Unchanged)) continue;
            if (lastFor.TryGetValue(r.Device.DeviceId, out var later)) { r.Outcome = Outcome.Repeated; r.SupersededBy = later.Line; }
            else lastFor[r.Device.DeviceId] = r;
        }
        return rows;
    }

    /// <summary>What Save sends: new notes always, overwrites only when the operator allowed them.</summary>
    public static DeviceNotesImport Request(IEnumerable<Row> rows, bool overwrite) => new()
    {
        Items = rows
            .Where(r => r.Device is not null && (r.Outcome == Outcome.New || (overwrite && r.Outcome == Outcome.Overwrite)))
            .Select(r => new DeviceNoteItem { DeviceId = r.Device!.DeviceId, Note = r.Note })
            .ToList(),
    };

    /// <summary>
    /// Decodes a file the way it was most likely saved. Excel's "CSV UTF-8" and "Unicode text" carry a byte-order
    /// mark. Its plain "CSV" is written in the Windows ANSI code page (1250 on Hungarian Windows), where ő and ű are
    /// single bytes that are not valid UTF-8 - so strict UTF-8 goes first and the ANSI code page is the fallback.
    /// </summary>
    public static (string Text, string EncodingName) Decode(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "UTF-8");
        if (bytes is [0xFF, 0xFE, ..]) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16");
        if (bytes is [0xFE, 0xFF, ..]) return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "UTF-16BE");
        try { return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "UTF-8"); }
        catch (DecoderFallbackException) { }
        var ansi = Ansi.Value;
        return (ansi.GetString(bytes), ansi.WebName);
    }

    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // .NET ships the legacy code pages but leaves them off
        int cp = GetACP();
        // With Windows' "UTF-8 for worldwide language support" the ANSI code page is UTF-8 itself, which the strict
        // attempt has already ruled out; the code page of the user's regional format is the next best guess.
        if (cp == 65001) cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        try { return Encoding.GetEncoding(cp); }
        catch (Exception) { return Encoding.Latin1; }
    });

    [DllImport("kernel32.dll")] private static extern int GetACP();

    // Excel wraps a cell in quotes when it holds the separator or a quote, and doubles the quotes inside.
    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\"\"", "\"").Trim() : s;

    private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "hostname", "host", "device", "devicename", "computer", "computername", "machine", "machinename", "name",
        "eszköz", "eszköznév", "eszkoznev", "gép", "gépnév", "gepnev", "számítógép", "szamitogep", "név", "nev",
    };

    // Only consulted for a first line that matched no device, so a machine really called "host" is still found.
    private static bool LooksLikeHeader(string host) =>
        HeaderWords.Contains(new string(host.Where(c => c is not (' ' or '_' or '-')).ToArray()));
}
