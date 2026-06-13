using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Helyi VNC-zár: ezen a gépen UAC-cal letiltható a távoli elérés (csak HELYBEN oldható fel).</summary>
public sealed class LocalLockView : UserControl, IContentView
{
    public Task OnShownAsync() { Refresh2(); return Task.CompletedTask; }
    public void ApplyTheme() { ThemeManager.StyleView(this); Refresh2(); }

    private readonly MaterialLabel _state = new();
    private readonly MaterialButton _toggle = new();
    private readonly MaterialLabel _status = new();

    public LocalLockView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24, 18, 24, 12);

        var title = new MaterialLabel { Text = "Helyi zár (távoli elérés tiltása ezen a gépen)", Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        var help = new MaterialLabel
        {
            Text = "Ha letiltod, erre a gépre senki sem tud távolról belépni (VNC), amíg HELYBEN (UAC-cal) fel nem oldod — távolról nem visszavonható.",
            AutoSize = true, MaximumSize = new Size(760, 0), Dock = DockStyle.Top,
        };
        _state.Font = new Font("Segoe UI", 12F, FontStyle.Bold); _state.AutoSize = true; _state.Dock = DockStyle.Top; _state.Margin = new Padding(0, 8, 0, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
        _toggle.Click += (_, _) => Toggle();
        buttons.Controls.Add(_toggle);

        // Állapot-üzenet sima címkeként (üresen láthatatlan — nincs szürke kártya).
        _status.AutoSize = true; _status.Dock = DockStyle.Top; _status.Margin = new Padding(0, 10, 0, 0);

        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(_state);
        Controls.Add(help);
        Controls.Add(title);
        Refresh2();
    }

    private void Refresh2()
    {
        bool locked = LocalVncLock.IsLocked();
        _state.Text = locked ? "Állapot:  LETILTVA (távolról nem elérhető)" : "Állapot:  Engedélyezve";
        _state.ForeColor = locked ? Color.IndianRed : Color.MediumSeaGreen;
        _toggle.Text = locked ? "Feloldás (UAC)" : "Letiltás (UAC)";
    }

    private void Toggle()
    {
        bool locked = LocalVncLock.IsLocked();
        var q = locked
            ? "Feloldod ezen a HELYI gépen a távoli elérést (VNC)?"
            : "Letiltod ezen a HELYI gépen a távoli elérést (VNC)?\n\nUtána erre a gépre senki sem tud távolról belépni, amíg HELYBEN fel nem oldod.";
        if (MessageBox.Show(q, "Helyi VNC-zár", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            if (LocalVncLock.RunElevated(!locked))
            {
                _status.Text = locked ? "Helyi gép feloldva." : "Helyi gép ZÁROLVA — távolról nem elérhető.";
                Refresh2();
            }
            else _status.Text = "A művelet nem fejeződött be (UAC megszakítva?).";
        }
        catch (Exception ex) { _status.Text = "Helyi zár hiba: " + ex.Message; }
    }
}
