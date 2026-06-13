using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Egy blob/token utólagos módosítása: lejárat és/vagy max telepítés. A „Változatlan" opciók nem nyúlnak a mezőhöz.</summary>
public sealed class EditTokenForm : MaterialForm
{
    private readonly MaterialComboBox _expiry = new() { Hint = "Lejárat" };
    private readonly MaterialComboBox _maxUses = new() { Hint = "Max telepítés" };

    public int? MaxUses { get; private set; }
    public int? ExpiresInHours { get; private set; }
    public bool ClearExpiry { get; private set; }

    private sealed record ExpiryItem(int? Hours, bool Clear, bool Keep, string Name) { public override string ToString() => Name; }
    private sealed record UsesItem(int? Max, bool Keep, string Name) { public override string ToString() => Name; }

    public EditTokenForm(BootstrapTokenInfo t)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = $"Blob módosítása — {t.Id.ToString("N")[..8]}";
        Sizable = false;
        Width = 420; Height = 300;
        StartPosition = FormStartPosition.CenterParent;

        var info = new MaterialLabel
        {
            Text = $"Eddig felhasználva: {t.UseCount} telepítés.\nA max nem állítható ez alá (a letiltás: Visszavonás).",
            AutoSize = false, Location = new Point(20, 70), Size = new Size(370, 44),
        };

        _expiry.Width = 360; _expiry.Location = new Point(20, 118);
        _expiry.Items.AddRange([
            new ExpiryItem(null, false, true, "Lejárat: változatlan"),
            new ExpiryItem(null, true, false, "Nincs lejárat"),
            new ExpiryItem(24, false, false, "Mostantól 24 óra"),
            new ExpiryItem(168, false, false, "Mostantól 7 nap"),
            new ExpiryItem(720, false, false, "Mostantól 30 nap"),
        ]);
        _expiry.SelectedIndex = 0;

        _maxUses.Width = 360; _maxUses.Location = new Point(20, 168);
        _maxUses.Items.Add(new UsesItem(null, true, "Max telepítés: változatlan"));
        _maxUses.Items.Add(new UsesItem(100000, false, "Korlátlan"));
        foreach (var n in new[] { 1, 5, 10, 50 })
            if (n >= t.UseCount) _maxUses.Items.Add(new UsesItem(n, false, n.ToString()));
        _maxUses.SelectedIndex = 0;

        var ok = new MaterialButton { Text = "Mentés", Location = new Point(212, 220), AutoSize = false, Width = 88 };
        var cancel = new MaterialButton { Text = "Mégse", Location = new Point(304, 220), AutoSize = false, Width = 86, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        ok.Click += (_, _) =>
        {
            var e = (ExpiryItem)_expiry.SelectedItem!;
            var u = (UsesItem)_maxUses.SelectedItem!;
            ClearExpiry = e.Clear;
            ExpiresInHours = e.Keep || e.Clear ? null : e.Hours;
            MaxUses = u.Keep ? null : u.Max;
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        Controls.AddRange([info, _expiry, _maxUses, ok, cancel]);
    }
}
