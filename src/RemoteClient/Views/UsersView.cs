using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

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
    private readonly MaterialTextBox2 _search = new() { Hint = L.UsersView_001, Width = 360 };
    private readonly List<UserInfo> _users = new();

    // Fülsáv
    private readonly MaterialButton _tabGeneral = TabBtn(L.ChannelsView_003);
    private readonly MaterialButton _tabLog = TabBtn("Log");
    private readonly MaterialButton _tabPassword = TabBtn(L.MainForm_001);
    private readonly MaterialButton _tabGrants = TabBtn(L.UsersView_002);
    private readonly MaterialButton _tabHello = TabBtn("Windows Hello");
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };

    // Általános fül
    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly MaterialTextBox2 _nameBox = new() { Hint = L.UsersView_003, Width = 380 };
    private readonly MaterialTextBox2 _emailBox = new() { Hint = L.ForgotPasswordForm_002, Width = 380 };
    private readonly MaterialComboBox _roleCombo = new() { Hint = L.NewUserForm_008, Width = 200 };
    private readonly MaterialSwitch _activeSwitch = new() { Text = L.BootstrapView_020, AutoSize = true };
    private readonly MaterialLabel _generalStatus = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };
    private Panel _generalPanel = null!;

    // Jelszó fül
    private readonly MaterialLabel _passwordStatus = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };
    private Panel _passwordPanel = null!;

    // Per-user fülek (a kiválasztott userhez újraépítve)
    private GrantsPanel? _grantsPanel;
    private HelloDevicesPanel? _helloPanel;
    private LogPanel? _logPanel;

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
        // Felső sáv: keresés + frissítés.
        var tools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 16, 0);
        _search.TextChanged += (_, _) => RenderList();
        tools.Controls.Add(_search);
        var refresh = ViewUi.ToolbarButton(L.AboutView_002, primary: false);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.Add(refresh);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.CredentialDialog_002, 140);
        _list.Columns.Add(L.UsersView_004, 160);
        _list.Columns.Add(L.NewUserForm_008, 80);
        _list.Columns.Add(L.BootstrapView_020, 55);
        _list.Columns.Add("TOTP", 55);
        _list.Columns.Add("Hello", 55);
        _list.Columns.Add(L.UsersView_005, 140);
        _list.DoubleClick += (_, _) => EditSelected();

        // A tábla alatt jobbra: Tulajdonságok; alatta egy sorral: Új User.
        var editRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 2) };
        var edit = ViewUi.ToolbarButton(L.DevicesView_007);
        edit.Margin = new Padding(4, 0, 4, 0);
        edit.Click += (_, _) => EditSelected();
        editRow.Controls.Add(edit);

        // Új User BALRA: lista-szintű művelet, nem függ a kijelölt sortól.
        var newRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 0, 8, 4) };
        var newUser = ViewUi.ToolbarButton(L.UsersView_006);
        newUser.Margin = new Padding(4, 0, 4, 0);
        newUser.Click += async (_, _) => await NewUserAsync();
        newRow.Controls.Add(newUser);

        _listHost.Controls.Add(ViewUi.Rows(1, tools, _list, editRow, newRow, ViewUi.StatusHost(_status)));
    }

    private void BuildEditor()
    {
        var back = ViewUi.ToolbarButton(L.ChannelsView_013, primary: false);
        back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        _tabGeneral.Click += async (_, _) => await SelectTabAsync("general");
        _tabLog.Click += async (_, _) => await SelectTabAsync("log");
        _tabPassword.Click += async (_, _) => await SelectTabAsync("password");
        _tabGrants.Click += async (_, _) => await SelectTabAsync("grants");
        _tabHello.Click += async (_, _) => await SelectTabAsync("hello");

        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, _tabGeneral, _tabLog, _tabPassword, _tabGrants, _tabHello]);

        _generalPanel = BuildGeneralPanel();
        _passwordPanel = BuildPasswordPanel();

        _editorHost.Controls.Add(ViewUi.Rows(2, tabbar, _editorTitle, _tabContent));
    }

    private Panel BuildGeneralPanel()
    {
        _roleCombo.Items.AddRange(["operator", "admin"]);
        var save = ViewUi.ToolbarButton(L.EditTokenForm_012);
        save.Click += async (_, _) => await SaveAsync();
        var revoke = ViewUi.ToolbarButton(L.UsersView_007, primary: false);
        revoke.Click += async (_, _) => await RevokeAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        _nameBox.Margin = new Padding(4, 8, 4, 8);
        _emailBox.Margin = new Padding(4, 8, 4, 8);
        _roleCombo.Margin = new Padding(4, 8, 4, 8);
        _activeSwitch.Margin = new Padding(4, 8, 4, 12);
        body.Controls.Add(_nameBox);
        body.Controls.Add(_emailBox);
        body.Controls.Add(_roleCombo);
        body.Controls.Add(_activeSwitch);
        body.Controls.Add(save);
        body.Controls.Add(new MaterialDivider { Width = 420, Margin = new Padding(4, 16, 4, 8) });
        body.Controls.Add(new MaterialLabel { Text = "Munkamenetek", FontType = MaterialSkinManager.fontType.Subtitle2, AutoSize = true, Margin = new Padding(4, 0, 0, 4) });
        body.Controls.Add(revoke);
        body.Controls.Add(_generalStatus);
        return body;
    }

    private readonly MaterialSwitch _resetEmailCode = new() { Text = L.NewUserForm_003, AutoSize = true, Checked = true };
    private readonly MaterialSwitch _resetClearTotp = new() { Text = L.UsersView_008, AutoSize = true };

    private Panel BuildPasswordPanel()
    {
        var reset = ViewUi.ToolbarButton(L.UsersView_009);
        reset.Click += async (_, _) => await ResetPwAsync();
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 12, 12, 8) };
        body.Controls.Add(new MaterialLabel { Text = L.UsersView_010, AutoSize = true, Margin = new Padding(4, 0, 4, 12) });
        body.Controls.Add(_resetEmailCode);
        body.Controls.Add(_resetClearTotp);
        body.Controls.Add(reset);

        body.Controls.Add(new MaterialDivider { Width = 420, Margin = new Padding(4, 16, 4, 8) });
        body.Controls.Add(new MaterialLabel { Text = L.UsersView_011, FontType = MaterialSkinManager.fontType.Subtitle2, AutoSize = true, Margin = new Padding(4, 0, 0, 2) });
        body.Controls.Add(new MaterialLabel { Text = L.UsersView_012, AutoSize = true, Margin = new Padding(4, 0, 4, 8) });
        var clearTotp = ViewUi.ToolbarButton(L.UsersView_013, primary: false);
        clearTotp.Click += async (_, _) => await ClearTotpAsync();
        body.Controls.Add(clearTotp);

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
        MessageBox.Show(L.Format(L.UsersView_014, action),
            L.UsersView_015, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var users = await _api.GetUsersAsync();
            _users.Clear(); _users.AddRange(users);
            RenderList();
            _status.Text = L.Format(L.UsersView_016, users.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<UserInfo> items = _users;
        if (q.Length > 0)
            items = _users.Where(u =>
                (u.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (u.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var u in items)
        {
            var item = new ListViewItem(u.Username) { Tag = u };
            item.SubItems.Add(u.Name ?? "—");
            item.SubItems.Add(u.Role);
            item.SubItems.Add(u.IsActive ? "igen" : L.UsersView_038);
            item.SubItems.Add(u.TotpConfirmed ? "ok" : "—");
            item.SubItems.Add(u.HelloCount > 0 ? u.HelloCount.ToString() : "—");
            item.SubItems.Add(u.LastLoginAt?.LocalDateTime.ToString("g") ?? "—");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void EditSelected()
    {
        if (Selected() is not { } u) { _status.Text = L.UsersView_017; return; }
        _editing = u;
        _editorTitle.Text = string.IsNullOrWhiteSpace(u.Name) ? u.Username : $"{u.Name}  ({u.Username})";
        _nameBox.Text = u.Name ?? "";
        _emailBox.Text = u.Email ?? "";
        _roleCombo.SelectedItem = u.Role == "admin" ? "admin" : "operator";
        _activeSwitch.Checked = u.IsActive;
        _generalStatus.Text = ""; _passwordStatus.Text = "";

        // Per-user fülek frissen
        _grantsPanel?.Dispose(); _helloPanel?.Dispose(); _logPanel?.Dispose();
        _grantsPanel = new GrantsPanel(_api, u.Id);
        _helloPanel = new HelloDevicesPanel(_api, u.Id);
        _logPanel = new LogPanel(_api, actor: u.Username);

        ShowEditor();
        _ = SelectTabAsync("general");
    }

    private async Task SelectTabAsync(string tab)
    {
        foreach (var (b, key) in new[] { (_tabGeneral, "general"), (_tabLog, "log"), (_tabPassword, "password"), (_tabGrants, "grants"), (_tabHello, "hello") })
            b.Type = key == tab ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;

        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general": _tabContent.Controls.Add(_generalPanel); break;
            case "log" when _logPanel is not null: _tabContent.Controls.Add(_logPanel); await _logPanel.ShownAsync(); break;
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
        if (IsSelf(u) && role == "operator") { _generalStatus.Text = L.UsersView_018; return; }
        if (IsSelf(u) && !active) { _generalStatus.Text = L.UsersView_019; return; }
        var email = _emailBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) { _generalStatus.Text = L.NewUserForm_007; return; }
        try
        {
            await _api.UpdateUserAsync(u.Id, role, active, _nameBox.Text.Trim(), _emailBox.Text.Trim());
            _generalStatus.Text = "Mentve.";
            _editing = new UserInfo { Id = u.Id, Username = u.Username, Name = _nameBox.Text.Trim(), Email = _emailBox.Text.Trim(), Role = role, IsActive = active, HelloCount = u.HelloCount };
            _editorTitle.Text = string.IsNullOrWhiteSpace(_editing.Name) ? u.Username : $"{_editing.Name}  ({u.Username})";
        }
        catch (Exception ex) { _generalStatus.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private async Task NewUserAsync()
    {
        using var f = new NewUserForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var r = await _api.CreateUserAsync(f.Username, f.Email, f.Role, f.FullName, f.EmailCode);
            using (var dlg = new CredentialDialog(L.NewUserForm_004, r.Username, r.ResetCode,
                L.UsersView_020,
                L.UsersView_021)) dlg.ShowDialog(this);
            _status.Text = r.EmailSent ? L.UsersView_022 : (f.EmailCode ? L.UsersView_023 : L.UsersView_024);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private async Task ClearTotpAsync()
    {
        if (_editing is not { } u) return;
        if (MessageBox.Show(L.Format(L.UsersView_025, u.Username), L.UsersView_013, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.ClearTotpAsync(u.Id); _passwordStatus.Text = L.UsersView_026; }
        catch (Exception ex) { _passwordStatus.Text = L.UsersView_027 + ex.Message; }
    }

    private async Task ResetPwAsync()
    {
        if (_editing is not { } u) return;
        if (MessageBox.Show(L.Format(L.UsersView_028, u.Username), L.UsersView_009, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var r = await _api.ResetPasswordAsync(u.Id, _resetEmailCode.Checked, _resetClearTotp.Checked);
            using (var dlg = new CredentialDialog(L.ForgotPasswordForm_006, r.Username, r.ResetCode,
                L.UsersView_020,
                L.UsersView_029)) dlg.ShowDialog(this);
            var totpNote = _resetClearTotp.Checked ? L.UsersView_030 : "";
            _passwordStatus.Text = (r.EmailSent ? L.UsersView_031 : (_resetEmailCode.Checked ? L.UsersView_032 : L.UsersView_033)) + totpNote;
        }
        catch (Exception ex) { _passwordStatus.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (_editing is not { } u) return;
        if (BlockSelf(u, L.UsersView_034)) return;
        if (MessageBox.Show(L.Format(L.UsersView_035, u.Username), L.UsersView_036, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeSessionsAsync(u.Id); _generalStatus.Text = L.Format(L.UsersView_037, u.Username); }
        catch (Exception ex) { _generalStatus.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }
}
