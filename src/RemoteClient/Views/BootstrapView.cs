using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Token nélküli telepítés: site-bootstrap blob generálása (vágólapra is), telepítési leírással.</summary>
public sealed class BootstrapView : UserControl, IContentView
{
    public Task OnShownAsync() => Task.CompletedTask;
    public void ApplyTheme() => ThemeManager.StyleView(this);

    private readonly AdminApi _api;
    private readonly MaterialMultiLineTextBox2 _blob = new() { ReadOnly = true };
    private readonly MaterialButton _genBtn = new() { Text = "Bootstrap blob generálása" };
    private readonly MaterialButton _copyBtn = new() { Text = "Másolás", Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false, Enabled = false };
    private readonly MaterialLabel _status = new();

    public BootstrapView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        Padding = new Padding(24, 18, 24, 12);

        var title = new MaterialLabel { Text = "Token nélküli telepítés (bootstrap)", Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        var help = new MaterialLabel
        {
            Text = "Generálj egy site-bootstrap blobot, amivel az ügyfélgép magától beléptet (Pending-be kerül, itt kell jóváhagyni).\n\nTelepítés az ügyfélnél (admin):\n    RemoteAgent.exe bootstrap <blob>\n    RemoteAgent.exe install-service",
            AutoSize = true, Dock = DockStyle.Top,
        };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
        _genBtn.Margin = new Padding(0, 0, 8, 0);
        _genBtn.Click += async (_, _) => await GenerateAsync();
        _copyBtn.Click += (_, _) => { try { Clipboard.SetText(_blob.Text); _status.Text = "Vágólapra másolva."; } catch { _status.Text = "A vágólap most foglalt."; } };
        buttons.Controls.AddRange([_genBtn, _copyBtn]);

        _blob.Dock = DockStyle.Fill;

        var bottom = new MaterialCard { Dock = DockStyle.Bottom, Height = 40, Margin = new Padding(0) };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 12, 0);
        bottom.Controls.Add(_status);

        Controls.Add(_blob);
        Controls.Add(buttons);
        Controls.Add(help);
        Controls.Add(title);
        Controls.Add(bottom);
    }

    private async Task GenerateAsync()
    {
        try
        {
            _genBtn.Enabled = false; _status.Text = "Generálás…";
            var blob = await _api.CreateBootstrapAsync(maxUses: 100000, expiresInHours: null);
            if (string.IsNullOrWhiteSpace(blob)) { _status.Text = "Üres válasz."; return; }
            _blob.Text = blob;
            _copyBtn.Enabled = true;
            try { Clipboard.SetText(blob); _status.Text = "Generálva és vágólapra másolva."; }
            catch { _status.Text = "Generálva (a vágólap most foglalt)."; }
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
        finally { _genBtn.Enabled = true; }
    }
}
