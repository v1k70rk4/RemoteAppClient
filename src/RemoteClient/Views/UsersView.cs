using System.Drawing;
using System.Drawing.Drawing2D;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Admin user management: redesigned list (avatar + role chip, owner-drawn) plus an in-window tabbed editor
/// (General / Password / Permissions / Log / Windows Hello / Trusted devices). See design_handoff.
/// </summary>
public sealed class UsersView : UserControl, IContentView
{
    private static readonly Color[] AvatarPalette =
    {
        ColorTranslator.FromHtml("#4d8df0"), ColorTranslator.FromHtml("#3ecf8e"), ColorTranslator.FromHtml("#a78bfa"),
        ColorTranslator.FromHtml("#f0b24b"), ColorTranslator.FromHtml("#ef6f95"), ColorTranslator.FromHtml("#2dd4bf"),
    };

    private readonly AdminApi _api;
    private readonly string _currentUser;

    private readonly Panel _listHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 12) };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 12, 22, 18), Visible = false };
    private readonly OwnerList _list = new(52);
    private readonly TextField _search = new(L.UsersView_SearchUsernameOrName, 340, false, "search");
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private readonly ContextMenuStrip _menu = UiMenu.Themed();
    private readonly List<UserInfo> _users = new();

    // Editor shell
    private readonly IconButton _back = new("chevron");
    private readonly TabStrip _tabs = new();
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private Panel _editorHeader = null!;

    // General tab
    private readonly TextField _nameBox = new("", 380);
    private readonly TextField _emailBox = new("", 380, mono: true);
    private readonly Segment _roleSeg = new("operator", "admin");
    private readonly UiToggle _activeToggle = new();
    private readonly UiToggle _keylessToggle = new();
    private readonly MaterialLabel _generalStatus = new() { AutoSize = true };
    private Panel _generalPanel = null!;

    // Password tab
    private readonly UiToggle _resetEmailCode = new(L.NewUserForm_SendResetCodeByEmail) { Checked = true };
    private readonly UiToggle _resetClearTotp = new(L.UsersView_AlsoClearTOTPAuthenticatorUser);
    private readonly MaterialLabel _passwordStatus = new() { AutoSize = true };
    private Panel _passwordPanel = null!;

    // Per-user tabs (rebuilt for the selected user)
    private GrantsPanel? _grantsPanel;
    private HelloDevicesPanel? _helloPanel;
    private TrustedDevicesPanel? _trustsPanel;
    private LogPanel? _logPanel;

    private UserInfo? _editing;

    public UsersView(AdminApi api, string currentUser)
    {
        _api = api; _currentUser = currentUser;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
    }

    private void BuildList()
    {
        _search.Location = new Point(0, 8);
        _search.Changed += (_, _) => RenderList();

        var newBtn = new UiButton(L.UsersView_AddNewUser, UiButton.Style.Filled, "plus");
        newBtn.Click += async (_, _) => await NewUserAsync();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = ThemeManager.Bg };
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(newBtn);
        toolbar.Resize += (_, _) => newBtn.Location = new Point(toolbar.Width - newBtn.Width, 8);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.CredentialDialog_User, 240),
            new OwnerList.Col(L.UsersView_Name, 190),
            new OwnerList.Col(L.NewUserForm_Role, 110),
            new OwnerList.Col(L.BootstrapView_Active, 80),
            new OwnerList.Col("TOTP", 70),
            new OwnerList.Col("Hello", 70),
            new OwnerList.Col(L.UsersView_LastUsed, 150));
        _list.PaintRow += PaintUserRow;
        _list.RowActivated += item => EditSelected(item: (UserInfo)item);
        _list.RowRightClicked += (_, pt) => _menu.Show(pt);

        void MenuTab(string text, string tab) => _menu.Items.Add(text, null, (_, _) => EditSelected(tab));
        MenuTab(L.DevicesView_Properties, "general");
        MenuTab(L.MainForm_Password, "password");
        MenuTab(L.UsersView_Permissions, "grants");
        MenuTab("Log", "log");
        MenuTab("Windows Hello", "hello");
        MenuTab(L.TrustedDevicesPanel_Title, "trusts");
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(L.DevicesView_Delete, null, async (_, _) => await DeleteSelectedAsync());

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        _listHost.Controls.Add(_list);
        _listHost.Controls.Add(statusHost);
        _listHost.Controls.Add(toolbar);
    }

    private void PaintUserRow(object? sender, RowPaintEventArgs e)
    {
        var u = (UserInfo)e.Item;
        var c0 = e.Cell(0);
        var av = new Rectangle(c0.Left, e.Cy - 15, 30, 30);
        DrawAvatar(e.G, av, Initials(u), AvatarColor(u.Username ?? ""));
        TextRenderer.DrawText(e.G, u.Username, UiFont.MonoSemi, new Rectangle(av.Right + 11, c0.Top, c0.Right - av.Right - 11, c0.Height),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        e.Text(1, u.Name ?? "—", UiFont.Body, ThemeManager.Text2);

        bool admin = u.Role == "admin";
        var (rfg, rbg) = admin ? (ThemeManager.Accent, ThemeManager.AccentSoft) : (ThemeManager.Text2, ThemeManager.Panel3);
        UiPaint.DrawPill(e.G, e.Cell(2).Left, e.Cy, u.Role, rfg, rbg, UiFont.Label, false);

        e.Text(3, u.IsActive ? L.Common_Yes : L.UsersView_NO, UiFont.Body, u.IsActive ? ThemeManager.OkFg : ThemeManager.Text3);
        e.Text(4, u.TotpConfirmed ? "ok" : "—", UiFont.Mono, u.TotpConfirmed ? ThemeManager.OkFg : ThemeManager.Text3);
        e.Text(5, u.HelloCount > 0 ? u.HelloCount.ToString() : "—", UiFont.Mono, ThemeManager.Text2);
        e.Text(6, u.LastLoginAt?.LocalDateTime.ToString("g") ?? "—", UiFont.MonoSmall, ThemeManager.Text3);
    }

    private void BuildEditor()
    {
        _back.SetBounds(0, 12, 36, 36);
        _back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        _editorHeader = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = ThemeManager.Bg };
        _editorHeader.Controls.Add(_back);
        _editorHeader.Paint += PaintEditorHeader;

        _tabs.SetTabs(new[]
        {
            ("general", L.ChannelsView_General), ("password", L.MainForm_Password), ("grants", L.UsersView_Permissions),
            ("log", "Log"), ("hello", "Windows Hello"), ("trusts", L.TrustedDevicesPanel_Title),
        }, "general");
        _tabs.TabSelected += key => _ = SelectTabAsync(key);

        _generalPanel = BuildGeneralPanel();
        _passwordPanel = BuildPasswordPanel();

        _editorHost.Controls.Add(_tabContent);
        _editorHost.Controls.Add(_tabs);
        _editorHost.Controls.Add(_editorHeader);
    }

    private void PaintEditorHeader(object? sender, PaintEventArgs e)
    {
        if (_editing is not { } u) return;
        var g = e.Graphics;
        DrawAvatar(g, new Rectangle(48, 12, 38, 38), Initials(u), AvatarColor(u.Username ?? ""));
        string name = string.IsNullOrWhiteSpace(u.Name) ? u.Username ?? "" : u.Name!;
        var ns = TextRenderer.MeasureText(g, name, UiFont.PageTitle, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, name, UiFont.PageTitle, new Rectangle(98, 18, 640, 24), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, $"({u.Username})", UiFont.Mono, new Rectangle(98 + ns.Width + 8, 21, 300, 22), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    private Panel BuildGeneralPanel()
    {
        const int cardW = 560, cw = cardW - 36;
        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        var g = new Panel();
        _nameBox.SetBounds(0, 20, cw, 38);
        _emailBox.SetBounds(0, 80, cw, 38);
        _roleSeg.Location = new Point(0, 144);
        g.Controls.Add(_nameBox);
        g.Controls.Add(_emailBox);
        g.Controls.Add(_roleSeg);
        g.Controls.Add(new SettingRow(L.BootstrapView_Active, L.UsersView_ActiveDesc, _activeToggle) { Location = new Point(0, 186), Size = new Size(cw, 50) });
        g.Controls.Add(new SettingRow(L.UsersView_KeylessOperator, L.UsersView_KeylessDesc, _keylessToggle) { Location = new Point(0, 236), Size = new Size(cw, 50) });
        save.Location = new Point(0, 298);
        _generalStatus.Location = new Point(2, 344);
        g.Controls.Add(save);
        g.Controls.Add(_generalStatus);
        g.Paint += (_, e) =>
        {
            void Lbl(string t, int y) => TextRenderer.DrawText(e.Graphics, t, UiFont.Label, new Rectangle(0, y, cw, 16), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Lbl(L.UsersView_Name, 2);
            Lbl(L.ForgotPasswordForm_EmailAddress, 62);
            Lbl(L.NewUserForm_Role, 124);
        };
        var generalCard = new Card(null, null, g) { Width = cardW, Height = 394, Margin = new Padding(0, 0, 0, 16) };

        var force = new UiButton(L.UsersView_ForceSignOut, UiButton.Style.Warn);
        force.Click += async (_, _) => await RevokeAsync();
        var sBody = new Panel();
        force.Location = new Point(0, 0);
        sBody.Controls.Add(force);
        var sessionsCard = new Card(L.UsersView_Sessions, L.UsersView_SessionsDesc, sBody, bodyHeight: 44) { Width = cardW };

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = ThemeManager.Bg, Padding = new Padding(0, 2, 0, 8) };
        panel.Controls.Add(generalCard);
        panel.Controls.Add(sessionsCard);
        return panel;
    }

    private Panel BuildPasswordPanel()
    {
        const int cardW = 560;
        var reset = new UiButton(L.UsersView_PasswordReset);
        reset.Click += async (_, _) => await ResetPwAsync();
        var pw = new Panel();
        _resetEmailCode.Location = new Point(0, 2);
        _resetClearTotp.Location = new Point(0, 38);
        reset.Location = new Point(0, 80);
        _passwordStatus.Location = new Point(2, 126);
        pw.Controls.Add(_resetEmailCode);
        pw.Controls.Add(_resetClearTotp);
        pw.Controls.Add(reset);
        pw.Controls.Add(_passwordStatus);
        var pwCard = new Card(L.UsersView_PasswordReset, L.UsersView_GeneratesAndDisplaysATemporary, pw, bodyHeight: 150) { Width = cardW, Margin = new Padding(0, 0, 0, 16) };

        var clearTotp = new UiButton(L.UsersView_ClearTOTP, UiButton.Style.Outline);
        clearTotp.Click += async (_, _) => await ClearTotpAsync();
        var tb = new Panel();
        clearTotp.Location = new Point(0, 0);
        tb.Controls.Add(clearTotp);
        var totpCard = new Card(L.UsersView_TOTPTwoFactorAuthentication, L.UsersView_IfTheAuthenticatorWasLost, tb, bodyHeight: 44) { Width = cardW };

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = ThemeManager.Bg, Padding = new Padding(0, 2, 0, 8) };
        panel.Controls.Add(pwCard);
        panel.Controls.Add(totpCard);
        return panel;
    }

    // --- avatar helpers ---
    private static Color AvatarColor(string s) => AvatarPalette[(int)((uint)s.GetHashCode() % AvatarPalette.Length)];

    private static string Initials(UserInfo u)
    {
        var name = (u.Name ?? "").Trim();
        if (name.Length > 0)
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}" : char.ToUpper(name[0]).ToString();
        }
        var un = (u.Username ?? "?").Trim();
        return un.Length >= 2 ? un[..2].ToUpperInvariant() : un.ToUpperInvariant();
    }

    private static void DrawAvatar(Graphics g, Rectangle r, string initials, Color bg)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, r);
        g.SmoothingMode = old;
        TextRenderer.DrawText(g, initials, UiFont.Label, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public void ApplyTheme()
    {
        BackColor = _listHost.BackColor = _editorHost.BackColor = ThemeManager.Bg;
        Invalidate(true);
    }

    public async Task OnShownAsync() { ShowList(); await RefreshAsync(); }

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private UserInfo? Selected() => _list.Selected as UserInfo;
    private bool IsSelf(UserInfo u) => string.Equals(u.Username, _currentUser, StringComparison.OrdinalIgnoreCase);

    private bool BlockSelf(UserInfo u, string action)
    {
        if (!IsSelf(u)) return false;
        MessageBox.Show(L.Format(L.UsersView_YouCannotPerformThisAction, action), L.UsersView_SelfLockoutPrevented, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return true;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var users = await _api.GetUsersAsync();
            _users.Clear(); _users.AddRange(users);
            RenderList();
            _status.Text = L.Format(L.UsersView_User, users.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private void RenderList()
    {
        var q = _search.Query;
        IEnumerable<UserInfo> items = _users;
        if (q.Length > 0)
            items = _users.Where(u =>
                (u.Username?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (u.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));

        _list.BeginUpdate();
        _list.Clear();
        foreach (var u in items) _list.Add(u);
        _list.EndUpdate();
    }

    private void EditSelected(string initialTab = "general", UserInfo? item = null)
    {
        var u = item ?? Selected();
        if (u is null) { _status.Text = L.UsersView_SelectAUser; return; }
        _editing = u;
        _nameBox.Value = u.Name ?? "";
        _emailBox.Value = u.Email ?? "";
        _roleSeg.SelectedIndex = u.Role == "admin" ? 1 : 0;
        _activeToggle.Checked = u.IsActive;
        _keylessToggle.Checked = u.KeylessOperator;
        _generalStatus.Text = ""; _passwordStatus.Text = "";

        _grantsPanel?.Dispose(); _helloPanel?.Dispose(); _trustsPanel?.Dispose(); _logPanel?.Dispose();
        _grantsPanel = new GrantsPanel(_api, u.Id);
        _helloPanel = new HelloDevicesPanel(_api, u.Id);
        _trustsPanel = new TrustedDevicesPanel(_api, u.Id);
        _logPanel = new LogPanel(_api, actor: u.Username);

        ShowEditor();
        _editorHeader.Invalidate();
        _ = SelectTabAsync(initialTab);
    }

    private async Task SelectTabAsync(string tab)
    {
        _tabs.SetActive(tab);
        _tabContent.Controls.Clear();
        switch (tab)
        {
            case "general": _tabContent.Controls.Add(_generalPanel); break;
            case "password": _tabContent.Controls.Add(_passwordPanel); break;
            case "log" when _logPanel is not null: _tabContent.Controls.Add(_logPanel); await _logPanel.ShownAsync(); break;
            case "grants" when _grantsPanel is not null: _tabContent.Controls.Add(_grantsPanel); await _grantsPanel.ShownAsync(); break;
            case "hello" when _helloPanel is not null: _tabContent.Controls.Add(_helloPanel); await _helloPanel.ShownAsync(); break;
            case "trusts" when _trustsPanel is not null: _tabContent.Controls.Add(_trustsPanel); await _trustsPanel.ShownAsync(); break;
        }
    }

    private async Task SaveAsync()
    {
        if (_editing is not { } u) return;
        var role = _roleSeg.SelectedIndex == 1 ? "admin" : "operator";
        var active = _activeToggle.Checked;
        if (IsSelf(u) && role == "operator") { _generalStatus.Text = L.UsersView_YouCannotDemoteYourself; return; }
        if (IsSelf(u) && !active) { _generalStatus.Text = L.UsersView_YouCannotDeactivateYourself; return; }
        var email = _emailBox.Value.Trim();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@')) { _generalStatus.Text = L.NewUserForm_EnterAValidEmailAddress; return; }
        try
        {
            var keyless = _keylessToggle.Checked;
            await _api.UpdateUserAsync(u.Id, role, active, _nameBox.Value.Trim(), email, keyless);
            _generalStatus.Text = L.Common_Saved;
            _editing = new UserInfo { Id = u.Id, Username = u.Username, Name = _nameBox.Value.Trim(), Email = email, Role = role, IsActive = active, KeylessOperator = keyless, HelloCount = u.HelloCount };
            _editorHeader.Invalidate();
        }
        catch (Exception ex) { _generalStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task NewUserAsync()
    {
        using var f = new NewUserForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var r = await _api.CreateUserAsync(f.Username, f.Email, f.Role, f.FullName, f.EmailCode);
            using (var dlg = new CredentialDialog(L.NewUserForm_NewUser, r.Username, r.ResetCode, L.UsersView_PasswordRecoveryToken, L.UsersView_TheUserEntersTheToken)) dlg.ShowDialog(this);
            _status.Text = r.EmailSent ? L.UsersView_CreatedTokenEmailed : (f.EmailCode ? L.UsersView_CreatedEmailWasNOTSent : L.UsersView_CreatedGiveTheTokenTo);
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task ClearTotpAsync()
    {
        if (_editing is not { } u) return;
        if (MessageBox.Show(L.Format(L.UsersView_ClearSTOTPAuthenticatorThey, u.Username), L.UsersView_ClearTOTP, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.ClearTotpAsync(u.Id); _passwordStatus.Text = L.UsersView_TOTPClearedItWillBe; }
        catch (Exception ex) { _passwordStatus.Text = L.UsersView_TOTPClearError + ex.Message; }
    }

    private async Task ResetPwAsync()
    {
        if (_editing is not { } u) return;
        if (MessageBox.Show(L.Format(L.UsersView_ResetSPassword, u.Username), L.UsersView_PasswordReset, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        try
        {
            var r = await _api.ResetPasswordAsync(u.Id, _resetEmailCode.Checked, _resetClearTotp.Checked);
            using (var dlg = new CredentialDialog(L.ForgotPasswordForm_PasswordRecovery, r.Username, r.ResetCode, L.UsersView_PasswordRecoveryToken, L.UsersView_TheUserEntersTheToken_2)) dlg.ShowDialog(this);
            var totpNote = _resetClearTotp.Checked ? L.UsersView_TOTPCleared : "";
            _passwordStatus.Text = (r.EmailSent ? L.UsersView_ResetTokenEmailed : (_resetEmailCode.Checked ? L.UsersView_ResetEmailWasNOTSent : L.UsersView_ResetGiveTheTokenTo)) + totpNote;
        }
        catch (Exception ex) { _passwordStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (_editing is not { } u) return;
        if (BlockSelf(u, L.UsersView_ForceSignOut_2)) return;
        if (MessageBox.Show(L.Format(L.UsersView_TerminateAllSessionsForImmediate, u.Username), L.UsersView_SignOutSessions, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeSessionsAsync(u.Id); _generalStatus.Text = L.Format(L.UsersView_SignedOut, u.Username); }
        catch (Exception ex) { _generalStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected() is not { } u) { _status.Text = L.UsersView_SelectAUser; return; }
        if (BlockSelf(u, L.DevicesView_Delete)) return;
        if (MessageBox.Show(L.Format(L.UsersView_DeleteUserConfirm, u.Username), L.DevicesView_Delete, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteUserAsync(u.Id); _status.Text = L.Format(L.UsersView_UserDeleted, u.Username); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
