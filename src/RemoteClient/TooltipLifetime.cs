using System.Runtime.InteropServices;
using System.Text;

namespace RemoteClient;

/// <summary>
/// Every tooltip of the console lives ten seconds at most. A tooltip is a topmost window of its own that only
/// its owner closes: the device list's native row tip closes when the pointer leaves the row, the tip WinForms
/// shows for a row that has the keyboard focus closes when the focus moves, a button's tip when the pointer
/// leaves the button. An owner that lost what it was showing the tip for - the row rebuilt or filtered away,
/// the view detached, the console no longer the active window - leaves the bubble on screen for good, above
/// every window, and the console has had a bubble of each kind do that. This sweeps the UI thread's tooltip
/// windows once a second: one visible for <see cref="MaxLife"/> is closed, and all of them are closed the moment
/// the console loses the active window. What a sweep closes is redrawn beneath, so no trace of it stays.
/// </summary>
internal sealed class TooltipLifetime : IDisposable
{
    public static readonly TimeSpan MaxLife = TimeSpan.FromSeconds(10);

    private readonly Form _form;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<IntPtr, DateTime> _seen = new();

    public TooltipLifetime(Form form)
    {
        _form = form;
        _timer.Tick += (_, _) => Sweep(closeAll: false);
        _form.Deactivate += OnDeactivate;
        _timer.Start();
    }

    private void OnDeactivate(object? sender, EventArgs e) => Sweep(closeAll: true);

    /// <summary>Closes every tooltip window of the UI thread now (the list calls this before it rebuilds its rows).</summary>
    public void CloseAll() => Sweep(closeAll: true);

    private void Sweep(bool closeAll)
    {
        if (_form.IsDisposed) return;
        var now = DateTime.UtcNow;
        var visible = new List<IntPtr>();
        try
        {
            EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
            {
                if (IsWindowVisible(h) && IsTooltip(h)) visible.Add(h);
                return true;
            }, IntPtr.Zero);
        }
        catch { return; /* cosmetic */ }

        bool closed = false;
        foreach (var h in visible)
        {
            if (!_seen.TryGetValue(h, out var since)) _seen[h] = since = now;
            if (!closeAll && now - since < MaxLife) continue;
            Close(h);
            closed = true;
        }
        foreach (var gone in _seen.Keys.Where(k => !visible.Contains(k)).ToList()) _seen.Remove(gone);
        if (closed) _form.Invalidate(true);   // the bubble's pixels must not survive it
    }

    private static bool IsTooltip(IntPtr h)
    {
        var cls = new StringBuilder(80);
        GetClassName(h, cls, cls.Capacity);
        // "tooltips_class32" bare (the list's own tip) and WinForms' registered form of it ("WindowsForms10.tooltips_class32.…").
        return cls.ToString().Contains("tooltips_class32", StringComparison.OrdinalIgnoreCase);
    }

    private static void Close(IntPtr h)
    {
        SendMessage(h, TTM_POP, IntPtr.Zero, IntPtr.Zero);
        if (IsWindowVisible(h)) ShowWindow(h, SW_HIDE);
    }

    public void Dispose()
    {
        _form.Deactivate -= OnDeactivate;
        _timer.Stop();
        _timer.Dispose();
    }

    private const int TTM_POP = 0x0400 + 28;
    private const int SW_HIDE = 0;
    private delegate bool EnumThreadWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint threadId, EnumThreadWindowsProc callback, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
