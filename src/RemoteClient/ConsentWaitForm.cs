using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>
/// Waits for the remote device response to a connection request (user approval), correlated by nonce.
/// pollozva a szervert. Outcome: granted/auto/denied/timeout/no-user/locked/cancelled.
/// </summary>
public sealed class ConsentWaitForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly string _nonce;
    private bool _cancelled;

    public string Outcome { get; private set; } = "timeout";

    public ConsentWaitForm(AdminApi api, string nonce)
    {
        _api = api; _nonce = nonce;
        ThemeManager.Skin.AddFormToManage(this);
        Text = L.ConsentWaitForm_001;
        Sizable = false;
        Width = 470; Height = 280;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new MaterialLabel
        {
            Text = L.ConsentWaitForm_002 +
                   L.ConsentWaitForm_003,
            AutoSize = false, Location = new Point(24, 80), Size = new Size(420, 128),
        };
        var cancel = new MaterialButton
        {
            Text = L.ConsentWaitForm_004, Location = new Point(346, 218), AutoSize = false, Width = 96,
            Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false,
        };
        cancel.Click += (_, _) => { _cancelled = true; Outcome = "cancelled"; DialogResult = DialogResult.Cancel; };

        Controls.AddRange([lbl, cancel]);
        Load += async (_, _) => await PollAsync();
    }

    private async Task PollAsync()
    {
        // About 36s, slightly above the agent's 30s WTS timeout, polling every 600 ms.
        for (int i = 0; i < 60 && !_cancelled; i++)
        {
            try
            {
                var o = await _api.GetAccessResultAsync(_nonce);
                if (!string.IsNullOrEmpty(o)) { Outcome = o; if (!IsDisposed) DialogResult = DialogResult.OK; return; }
            }
            catch { /* transient; retry */ }
            await Task.Delay(600);
        }
        if (!_cancelled && !IsDisposed) { Outcome = "timeout"; DialogResult = DialogResult.OK; }
    }
}
