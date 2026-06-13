using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>
/// Megvárja a távoli gép válaszát a csatlakozás-kérésre (a felhasználó jóváhagyását), nonce alapján
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
        Text = "Várakozás a válaszra";
        Sizable = false;
        Width = 470; Height = 280;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;

        var lbl = new MaterialLabel
        {
            Text = "Várakozás a távoli gép felhasználójának jóváhagyására…\n\n" +
                   "A gépnél ülő felhasználónak egy ablakban engedélyeznie kell a csatlakozást.",
            AutoSize = false, Location = new Point(24, 80), Size = new Size(420, 128),
        };
        var cancel = new MaterialButton
        {
            Text = "Mégse", Location = new Point(346, 218), AutoSize = false, Width = 96,
            Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false,
        };
        cancel.Click += (_, _) => { _cancelled = true; Outcome = "cancelled"; DialogResult = DialogResult.Cancel; };

        Controls.AddRange([lbl, cancel]);
        Load += async (_, _) => await PollAsync();
    }

    private async Task PollAsync()
    {
        // ~36 mp (az agent 30 mp-es WTS-timeoutjánál bővebb), 600 ms-onként.
        for (int i = 0; i < 60 && !_cancelled; i++)
        {
            try
            {
                var o = await _api.GetAccessResultAsync(_nonce);
                if (!string.IsNullOrEmpty(o)) { Outcome = o; if (!IsDisposed) DialogResult = DialogResult.OK; return; }
            }
            catch { /* tranziens — próbáljuk újra */ }
            await Task.Delay(600);
        }
        if (!_cancelled && !IsDisposed) { Outcome = "timeout"; DialogResult = DialogResult.OK; }
    }
}
