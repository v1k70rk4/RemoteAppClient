using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>Admin user-kezelő: létrehozás, szerep, aktiválás, jelszó-reset, grantok, kitiltás.</summary>
public sealed class UsersForm : Form
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly Label _status = new();

    public UsersForm(AdminApi api)
    {
        _api = api;
        Text = "Felhasználók";
        Width = 770; Height = 470;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.Dock = DockStyle.Top; _list.Height = 320;
        _list.Columns.Add("Felhasználó", 160);
        _list.Columns.Add("Szerep", 80);
        _list.Columns.Add("Aktív", 55);
        _list.Columns.Add("Jelszócsere", 90);
        _list.Columns.Add("TOTP", 55);
        _list.Columns.Add("Utolsó belépés", 150);

        Btn("Új user…", 12, NewUserAsync);
        Btn("Szerep váltás", 132, ToggleRoleAsync);
        Btn("Aktív ki/be", 252, ToggleActiveAsync);
        Btn("Jelszó reset", 372, ResetPwAsync);
        Btn("Grantok…", 492, () => { Grants(); return Task.CompletedTask; });
        Btn("Kitiltás", 612, RevokeAsync);

        _status.SetBounds(12, 372, 740, 50);
        Controls.Add(_status);

        Load += async (_, _) => await RefreshAsync();
    }

    private void Btn(string text, int x, Func<Task> onClick)
    {
        var b = new Button { Text = text, Bounds = new Rectangle(x, 330, 114, 32) };
        b.Click += async (_, _) => await onClick();
        Controls.Add(b);
    }

    private UserInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (UserInfo)_list.SelectedItems[0].Tag!;

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
            MessageBox.Show(
                $"Létrehozva: {r.Username}\n\nIDEIGLENES jelszó:\n{r.TempPassword}\n\nAdd át a felhasználónak. Első belépéskor jelszót cserél és TOTP-t állít be.",
                "Új felhasználó", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ToggleRoleAsync()
    {
        if (Selected() is not { } u) return;
        var newRole = u.Role == "admin" ? "operator" : "admin";
        if (MessageBox.Show($"{u.Username} szerepe → {newRole}?", "Szerep váltás", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { await _api.UpdateUserAsync(u.Id, newRole, null); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ToggleActiveAsync()
    {
        if (Selected() is not { } u) return;
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
            MessageBox.Show($"{r.Username} új IDEIGLENES jelszava:\n{r.TempPassword}\n\nElső belépéskor cserélni kell.", "Jelszó reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        if (MessageBox.Show($"{u.Username} összes munkamenetének megszüntetése (azonnali kiléptetés)?", "Kitiltás", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try { await _api.RevokeSessionsAsync(u.Id); _status.Text = $"{u.Username} kiléptetve."; }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
