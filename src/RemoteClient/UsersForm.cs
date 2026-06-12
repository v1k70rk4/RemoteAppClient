using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Admin user-kezelő: létrehozás, szerep, aktiválás, jelszó-reset, grantok, kitiltás.</summary>
public sealed class UsersForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly string _currentUser;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    public UsersForm(AdminApi api, string currentUser)
    {
        _api = api;
        _currentUser = currentUser;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Felhasználók";
        Sizable = false;
        Width = 800; Height = 540;
        StartPosition = FormStartPosition.CenterParent;

        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(8, 9, 8, 0), WrapContents = false };
        void Btn(string text, Func<Task> onClick)
        {
            var b = new MaterialButton { Text = text, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
            b.Click += async (_, _) => await onClick();
            tools.Controls.Add(b);
        }
        Btn("Új user…", NewUserAsync);
        Btn("Szerep váltás", ToggleRoleAsync);
        Btn("Aktív ki/be", ToggleActiveAsync);
        Btn("Jelszó reset", ResetPwAsync);
        Btn("Grantok…", () => { Grants(); return Task.CompletedTask; });
        Btn("Kitiltás", RevokeAsync);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Felhasználó", 160);
        _list.Columns.Add("Szerep", 80);
        _list.Columns.Add("Aktív", 55);
        _list.Columns.Add("Jelszócsere", 90);
        _list.Columns.Add("TOTP", 55);
        _list.Columns.Add("Utolsó belépés", 150);
        ThemeManager.StyleList(_list);

        var bottom = new MaterialCard { Dock = DockStyle.Bottom, Height = 48, Margin = new Padding(0) };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 12, 0);
        bottom.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(tools);

        Load += async (_, _) => await RefreshAsync();
    }

    private UserInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (UserInfo)_list.SelectedItems[0].Tag!;

    /// <summary>True, ha a kijelölt user maga a bejelentkezett admin — ekkor a veszélyes művelet tilos.</summary>
    private bool IsSelf(UserInfo u) => string.Equals(u.Username, _currentUser, StringComparison.OrdinalIgnoreCase);

    private bool BlockSelf(UserInfo u, string action)
    {
        if (!IsSelf(u)) return false;
        MessageBox.Show($"Saját magadon nem végezhető el ez a művelet ({action}) — különben kizárnád magad.\nKérj meg egy másik admint.",
            "Önkizárás megelőzve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var users = await _api.GetUsersAsync();
            _list.Items.Clear();
            foreach (var u in users)
            {
                var item = new ListViewItem(u.Username) { Tag = u };
                item.SubItems.Add(u.Role);
                item.SubItems.Add(u.IsActive ? "igen" : "NEM");
                item.SubItems.Add(u.MustChangePassword ? "kell" : "—");
                item.SubItems.Add(u.TotpConfirmed ? "ok" : "—");
                item.SubItems.Add(u.LastLoginAt?.LocalDateTime.ToString("g") ?? "—");
                _list.Items.Add(item);
            }
            _status.Text = $"{users.Count} felhasználó.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task NewUserAsync()
    {
        using var f = new NewUserForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var r = await _api.CreateUserAsync(f.Username, f.Email, f.Role);
            using (var dlg = new CredentialDialog("Új felhasználó", r.Username, r.TempPassword)) dlg.ShowDialog(this);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ToggleRoleAsync()
    {
        if (Selected() is not { } u) return;
        var newRole = u.Role == "admin" ? "operator" : "admin";
        if (newRole == "operator" && BlockSelf(u, "lefokozás")) return;
        if (MessageBox.Show($"{u.Username} szerepe → {newRole}?", "Szerep váltás", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { await _api.UpdateUserAsync(u.Id, newRole, null); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ToggleActiveAsync()
    {
        if (Selected() is not { } u) return;
        if (u.IsActive && BlockSelf(u, "deaktiválás")) return;
        if (u.IsActive && MessageBox.Show($"Biztosan deaktiválod {u.Username} felhasználót?\n\nAzonnal kiléptetjük és nem tud belépni, amíg vissza nem aktiválod.",
                "Deaktiválás", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.UpdateUserAsync(u.Id, null, !u.IsActive); _status.Text = u.IsActive ? "Deaktiválva (kiléptetve)." : "Aktiválva."; await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ResetPwAsync()
    {
        if (Selected() is not { } u) return;
        if (MessageBox.Show($"{u.Username} jelszavának resetelése?", "Jelszó reset", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var r = await _api.ResetPasswordAsync(u.Id);
            using (var dlg = new CredentialDialog("Jelszó reset", r.Username, r.TempPassword)) dlg.ShowDialog(this);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private void Grants()
    {
        if (Selected() is not { } u) return;
        using var f = new GrantsForm(_api, u.Id, u.Username);
        f.ShowDialog(this);
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } u) return;
        if (BlockSelf(u, "kiléptetés")) return;
        if (MessageBox.Show($"{u.Username} összes munkamenetének megszüntetése (azonnali kiléptetés)?", "Kitiltás", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeSessionsAsync(u.Id); _status.Text = $"{u.Username} kiléptetve."; }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
