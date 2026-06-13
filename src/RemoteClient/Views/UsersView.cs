using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>
/// Admin user-kezelő: lista + ablakon BELÜLI, FÜLES szerkesztő (nem külön ablak).
/// Felül: „← Vissza | Általános | Jelszó | Jogosultságok | Windows Hello".
/// </summary>
public sealed class UsersView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly string _currentUser;

    private readonly Panel _listHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    // Fülsáv
    private readonly MaterialButton _tabGeneral = TabBtn("Általános");
    private readonly MaterialButton _tabPassword = TabBtn("Jelszó");
    private readonly MaterialButton _tabGrants = TabBtn("Jogosultságok");
    private readonly MaterialButton _tabHello = TabBtn("Windows Hello");
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };

    // Általános fül
    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly MaterialTextBox2 _nameBox = new() { Hint = "Megjelenítendő név (pl. Gipsz Jakab)", Width = 380 };
    private readonly MaterialComboBox _roleCombo = new() { Hint = "Szerep", Width = 200 };
    private readonly MaterialSwitch _activeSwitch = new() { Text = "Aktív", AutoSize = true };
    private readonly MaterialLabel _generalStatus = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };
    private Panel _generalPanel = null!;

    // Jelszó fül
    private readonly MaterialLabel _passwordStatus = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };
    private Panel _passwordPanel = null!;

    // Per-user fülek (a kiválasztott userhez újraépítve)
    private GrantsPanel? _grantsPanel;
    private HelloDevicesPanel? _helloPanel;

    private UserInfo? _editing;

    public UsersView(AdminApi api, string currentUser)
    {
        _api = api; _currentUser = currentUser;
        Dock = DockStyle.Fill;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
        ApplyTheme();
    }

    private static MaterialButton TabBtn(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(4, 0, 0, 0), Type = MaterialButton.MaterialButtonType.Text };

    private void BuildList()
    {
        var tools = ViewUi.Toolbar();
        void Btn(string text, bool primary, Func<Task> onClick)
        {
            var b = ViewUi.ToolbarButton(text, primary);
            b.Click += async (_, _) => await onClick();
            tools.Controls.Add(b);
        }
        Btn("Új user…", true, NewUserAsync);
        Btn("Szerkesztés", true, () => { EditSelected(); return Task.CompletedTask; });
        Btn("Frissítés", false, RefreshAsync);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Felhasználó", 140);
        _list.Columns.Add("Név", 160);
        _list.Columns.Add("Szerep", 80);
        _list.Columns.Add("Aktív", 55);
        _list.Columns.Add("TOTP", 55);
        _list.Columns.Add("Hello", 55);
        _list.Columns.Add("Utoljára", 140);
        _list.DoubleClick += (_, _) => EditSelected();

        _listHost.Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
    }

    private void BuildEditor()
    {
        var back = ViewUi.ToolbarButton("← Vissza", primary: false);
        back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        _tabGeneral.Click += async (_, _) => await SelectTabAsync("general");
        _tabPassword.Click += async (_, _) => await SelectTabAsync("password");
        _tabGrants.Click += async (_, _) => await SelectTabAsync("grants");
        _tabHello.Click += async (_, _) => await SelectTabAsync("hello");

        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, _tabGeneral, _tabPassword, _tabGrants, _tabHello]);

        _generalPanel = BuildGeneralPanel();
        _passwordPanel = BuildPasswordPanel();

        _editorHost.Controls.Add(ViewUi.Rows(2, tabbar, _editorTitle, _tabContent));
    }

    private Panel BuildGeneralPanel()
    {
        _roleCombo.Items.AddRange(["operator", "admin"]);
        var save = ViewUi.ToolbarButton("Mentés");
        save.Click += async (_, _) => await SaveAsync();
        var revoke = ViewUi.ToolbarButton("Kitiltás (kiléptetés)", primary: false);
        revoke.Click += async (_, _) => await RevokeAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        _nameBox.Margin = new Padding(4, 8, 4, 8);
        _roleCombo.Margin = new Padding(4, 8, 4, 8);
        _activeSwitch.Margin = new Padding(4, 8, 4, 12);
        body.Controls.Add(_nameBox);
        body.Controls.Add(_roleCombo);
        body.Controls.Add(_activeSwitch);
        body.Controls.Add(save);
        body.Controls.Add(new MaterialDivider { Width = 420, Margin = new Padding(4, 16, 4, 8) });
        body.Controls.Add(new MaterialLabel { Text = "Munkamenetek", FontType = MaterialSkinManager.fontType.Subtitle2, AutoSize = true, Margin = new Padding(4, 0, 0, 4) });
        body.Controls.Add(revoke);
        body.Controls.Add(_generalStatus);
        return body;
    }

    private Panel BuildPasswordPanel()
    {
        var reset = ViewUi.ToolbarButton("Jelszó reset");
        reset.Click += async (_, _) => await ResetPwAsync();
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 12, 12, 8) };
        body.Controls.Add(new MaterialLabel { Text = "Ideiglenes jelszót generál és kiírja. A felhasználónak a következő belépéskor cserélnie kell.", AutoSize = true, Margin = new Padding(4, 0, 4, 12) });
        body.Controls.Add(reset);
        body.Controls.Add(_passwordStatus);
        return body;
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() { ShowList(); await RefreshAsync(); }

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private UserInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (UserInfo)_list.SelectedItems[0].Tag!;
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
                item.SubItems.Add(u.Name ?? "—");
                item.SubItems.Add(u.Role);
                item.SubItems.Add(u.IsActive ? "igen" : "NEM");
                item.SubItems.Add(u.TotpConfirmed ? "ok" : "—");
                item.SubItems.Add(u.HelloCount > 0 ? u.HelloCount.ToString() : "—");
                item.SubItems.Add(u.LastLoginAt?.LocalDateTime.ToString("g") ?? "—");
                _list.Items.Add(item);
            }
            _status.Text = $"{users.Count} felhasználó.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private void EditSelected()
    {
        if (Selected() is not { } u) { _status.Text = "Válassz egy felhasználót."; return; }
        _editing = u;
        _editorTitle.Text = string.IsNullOrWhiteSpace(u.Name) ? u.Username : $"{u.Name}  ({u.Username})";
        _nameBox.Text = u.Name ?? "";
        _roleCombo.SelectedItem = u.Role == "admin" ? "admin" : "operator";
        _activeSwitch.Checked = u.IsActive;
        _generalStatus.Text = ""; _passwordStatus.Text = "";

        // Per-user fülek frissen
        _grantsPanel?.Dispose(); _helloPanel?.Dispose();
        _grantsPanel = new GrantsPanel(_api, u.Id);
        _helloPanel = new HelloDevicesPanel(_api, u.Id);

        ShowEditor();
        _ = SelectTabAsync("general");
    }

    private async Task SelectTabAsync(string tab)
    {
        foreach (var (b, key) in new[] { (_tabGeneral, "general"), (_tabPassword, "password"), (_tabGrants, "grants"), (_tabHello, "hello") })
            b.Type = key == tab ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;

        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general": _tabContent.Controls.Add(_generalPanel); break;
            case "password": _tabContent.Controls.Add(_passwordPanel); break;
            case "grants" when _grantsPanel is not null: _tabContent.Controls.Add(_grantsPanel); await _grantsPanel.ShownAsync(); break;
            case "hello" when _helloPanel is not null: _tabContent.Controls.Add(_helloPanel); await _helloPanel.ShownAsync(); break;
        }
    }

    private async Task SaveAsync()
    {
        if (_editing is not { } u) return;
        var role = (_roleCombo.SelectedItem as string) ?? u.Role;
        var active = _activeSwitch.Checked;
        if (IsSelf(u) && role == "operator") { _generalStatus.Text = "Saját magad nem fokozhatod le."; return; }
        if (IsSelf(u) && !active) { _generalStatus.Text = "Saját magad nem deaktiválhatod."; return; }
        try
        {
            await _api.UpdateUserAsync(u.Id, role, active, _nameBox.Text.Trim());
            _generalStatus.Text = "Mentve.";
            _editing = new UserInfo { Id = u.Id, Username = u.Username, Name = _nameBox.Text.Trim(), Role = role, IsActive = active, HelloCount = u.HelloCount };
            _editorTitle.Text = string.IsNullOrWhiteSpace(_editing.Name) ? u.Username : $"{_editing.Name}  ({u.Username})";
        }
        catch (Exception ex) { _generalStatus.Text = "Hiba: " + ex.Message; }
    }

    private async Task NewUserAsync()
    {
        using var f = new NewUserForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var r = await _api.CreateUserAsync(f.Username, f.Email, f.Role, f.Name);
            using (var dlg = new CredentialDialog("Új felhasználó", r.Username, r.TempPassword)) dlg.ShowDialog(this);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task ResetPwAsync()
    {
        if (_editing is not { } u) return;
        if (MessageBox.Show($"{u.Username} jelszavának resetelése?", "Jelszó reset", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var r = await _api.ResetPasswordAsync(u.Id);
            using (var dlg = new CredentialDialog("Jelszó reset", r.Username, r.TempPassword)) dlg.ShowDialog(this);
            _passwordStatus.Text = "Jelszó resetelve.";
        }
        catch (Exception ex) { _passwordStatus.Text = "Hiba: " + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (_editing is not { } u) return;
        if (BlockSelf(u, "kiléptetés")) return;
        if (MessageBox.Show($"{u.Username} összes munkamenetének megszüntetése (azonnali kiléptetés)?", "Kitiltás", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeSessionsAsync(u.Id); _generalStatus.Text = $"{u.Username} kiléptetve."; }
        catch (Exception ex) { _generalStatus.Text = "Hiba: " + ex.Message; }
    }
}
