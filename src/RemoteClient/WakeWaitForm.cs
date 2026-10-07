using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Waits for a sleeping device to wake up and answer a queued connect, correlated by nonce. The server
/// holds the command and delivers it the moment the device reports in; the agent opens the tunnel in the
/// first second of that wake, which is the only way to catch a laptop that dozes off again within a minute.
/// Outcome: the device's answer (granted/auto/denied/...), "cancelled", or "timeout" after half an hour.
/// </summary>
public sealed class WakeWaitForm : MaterialForm
{
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private readonly AdminApi _api;
    private readonly string _nonce;
    private readonly string _host;
    private readonly MaterialLabel _lbl;
    private bool _cancelled;

    public string Outcome { get; private set; } = "timeout";

    public WakeWaitForm(AdminApi api, string nonce, string host)
    {
        _api = api; _nonce = nonce; _host = host;
        // NOT AddFormToManage (see ConsentWaitForm): the shared scheme themes the controls, the bg is set to match.
        BackColor = ThemeManager.Background;
        Text = L.WakeWaitForm_Title;
        Sizable = false;
        Width = 470; Height = 280;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        _lbl = new MaterialLabel { AutoSize = false, Location = new Point(24, 80), Size = new Size(420, 128) };
        Render(TimeSpan.Zero);
        var cancel = new MaterialButton
        {
            Text = L.ConsentWaitForm_Cancel, Location = new Point(346, 218), AutoSize = false, Width = 96,
            Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false,
        };
        cancel.Click += (_, _) => { _cancelled = true; Outcome = "cancelled"; DialogResult = DialogResult.Cancel; };

        Controls.AddRange([_lbl, cancel]);
        Load += async (_, _) => await PollAsync();
    }

    private void Render(TimeSpan elapsed) =>
        _lbl.Text = L.Format(L.WakeWaitForm_Text, _host, $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}");

    private async Task PollAsync()
    {
        var started = DateTime.UtcNow;
        while (!_cancelled && !IsDisposed)
        {
            var elapsed = DateTime.UtcNow - started;
            if (elapsed > MaxWait) break;
            Render(elapsed);
            try
            {
                var o = await _api.GetAccessResultAsync(_nonce);
                if (!string.IsNullOrEmpty(o)) { Outcome = o; if (!IsDisposed) DialogResult = DialogResult.OK; return; }
            }
            catch { /* transient; the next poll may succeed */ }
            await Task.Delay(Poll);
        }
        if (!_cancelled && !IsDisposed) { Outcome = "timeout"; DialogResult = DialogResult.OK; }
    }
}
