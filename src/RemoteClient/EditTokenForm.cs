using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>Edits an existing blob/token: expiry and/or max installs. "Unchanged" options leave the field untouched.</summary>
public sealed class EditTokenForm : MaterialForm
{
    private readonly MaterialComboBox _expiry = new() { Hint = L.EditTokenForm_001 };
    private readonly MaterialComboBox _maxUses = new() { Hint = L.EditTokenForm_002 };

    public int? MaxUses { get; private set; }
    public int? ExpiresInHours { get; private set; }
    public bool ClearExpiry { get; private set; }

    private sealed record ExpiryItem(int? Hours, bool Clear, bool Keep, string Name) { public override string ToString() => Name; }
    private sealed record UsesItem(int? Max, bool Keep, string Name) { public override string ToString() => Name; }

    public EditTokenForm(BootstrapTokenInfo t)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = L.Format(L.EditTokenForm_003, t.Id.ToString("N")[..8]);
        Sizable = false;
        Width = 420; Height = 300;
        StartPosition = FormStartPosition.CenterParent;

        var info = new MaterialLabel
        {
            Text = L.Format(L.EditTokenForm_004, t.UseCount),
            AutoSize = false, Location = new Point(20, 70), Size = new Size(370, 44),
        };

        _expiry.Width = 360; _expiry.Location = new Point(20, 118);
        _expiry.Items.AddRange([
            new ExpiryItem(null, false, true, L.EditTokenForm_005),
            new ExpiryItem(null, true, false, L.EditTokenForm_006),
            new ExpiryItem(24, false, false, L.EditTokenForm_007),
            new ExpiryItem(168, false, false, L.EditTokenForm_008),
            new ExpiryItem(720, false, false, L.EditTokenForm_009),
        ]);
        _expiry.SelectedIndex = 0;

        _maxUses.Width = 360; _maxUses.Location = new Point(20, 168);
        _maxUses.Items.Add(new UsesItem(null, true, L.EditTokenForm_010));
        _maxUses.Items.Add(new UsesItem(100000, false, L.EditTokenForm_011));
        foreach (var n in new[] { 1, 5, 10, 50 })
            if (n >= t.UseCount) _maxUses.Items.Add(new UsesItem(n, false, n.ToString()));
        _maxUses.SelectedIndex = 0;

        var ok = new MaterialButton { Text = L.EditTokenForm_012, Location = new Point(212, 220), AutoSize = false, Width = 88 };
        var cancel = new MaterialButton { Text = L.ConsentWaitForm_004, Location = new Point(304, 220), AutoSize = false, Width = 86, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
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
