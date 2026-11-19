using System.Globalization;

namespace RemoteClient;

/// <summary>
/// Copy speed over the last few seconds and the time left at that speed. Sampled on a timer rather than on
/// every read, so a stalled transfer shows its speed falling to zero instead of the last good figure.
/// </summary>
public sealed class TransferMeter(TimeSpan window)
{
    private readonly List<(TimeSpan At, long Bytes)> _samples = [];

    public TransferMeter() : this(TimeSpan.FromSeconds(3)) { }

    /// <summary>Forget the samples: a new file starts from zero.</summary>
    public void Reset() => _samples.Clear();

    /// <summary>
    /// Records <paramref name="bytes"/> copied by <paramref name="at"/> and returns bytes per second over the
    /// window; null until the samples span half a second.
    /// </summary>
    public double? Sample(TimeSpan at, long bytes)
    {
        _samples.Add((at, bytes));
        // Drop the oldest while the next one alone still covers the window: the span stays just over it.
        while (_samples.Count > 2 && at - _samples[1].At >= window) _samples.RemoveAt(0);
        var (firstAt, firstBytes) = _samples[0];
        var span = (at - firstAt).TotalSeconds;
        return span < 0.5 ? null : Math.Max(0, bytes - firstBytes) / span;
    }

    /// <summary>Time left for the rest at this speed; null when there is no speed or nothing left.</summary>
    public static TimeSpan? Remaining(long done, long total, double? bytesPerSecond) =>
        bytesPerSecond is > 0 && total > done
            ? TimeSpan.FromSeconds(Math.Min((total - done) / bytesPerSecond.Value, TimeSpan.FromDays(1).TotalSeconds))
            : null;

    /// <summary>A size for people: 0 B, 512 KB, 12.3 MB, 1.25 GB (the culture's decimal separator).</summary>
    public static string Size(double bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        var fmt = u == 0 ? "0" : bytes >= 100 ? "0" : bytes >= 10 ? "0.0" : "0.00";
        return bytes.ToString(fmt, culture) + " " + units[u];
    }

    /// <summary>A duration as m:ss, or h:mm:ss from an hour up.</summary>
    public static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
