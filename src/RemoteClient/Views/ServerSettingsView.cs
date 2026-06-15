using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Server-level admin settings: General tab (owner + support) and Email sending tab
/// (SMTP or MS Graph app-only) with test sending. Save lives at the bottom.
/// </summary>
public sealed class ServerSettingsView : UserControl, IContentView
{
    private readonly AdminApi _api;

    private readonly MaterialButton _tabGeneral = TabBtn(L.ChannelsView_General);
    private readonly MaterialButton _tabEmail = TabBtn(L.ServerSettingsView_EmailDelivery);
    private readonly MaterialButton _tabServerUpdate = TabBtn(L.ServerSettingsView_ServerUpdate);
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private FlowLayoutPanel _saveRow = null!;
    private readonly MaterialLabel _status = new();

    // Server update tab
    private readonly MaterialLabel _updVersion = new() { AutoSize = true, Margin = new Padding(4, 6, 0, 0) };
    private readonly TextBox _updResult = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F) };
    private MaterialButton _updBtn = null!;
    private MaterialButton _rbBtn = null!;

    // General
    private readonly MaterialTextBox2 _owner = new() { Hint = L.ServerSettingsView_OwnerName, Width = 360 };
    private readonly MaterialTextBox2 _phone = new() { Hint = L.ServerSettingsView_SupportPhoneNumber, Width = 360 };
    private readonly MaterialTextBox2 _email = new() { Hint = L.ServerSettingsView_SupportEmail, Width = 360 };
    private readonly MaterialComboBox _language = new() { Hint = L.ServerSettingsView_MessageLanguage, Width = 260 };

    private sealed record LangItem(string Code, string Name) { public override string ToString() => Name; }

    // Email provider + fields
    private readonly MaterialComboBox _provider = new() { Hint = "E-mail provider", Width = 260 };
    private readonly Panel _smtpBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel _graphBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private readonly MaterialTextBox2 _smtpHost = new() { Hint = "SMTP host", Width = 360 };
    private readonly MaterialTextBox2 _smtpPort = new() { Hint = "Port", Width = 120 };
    private readonly MaterialSwitch _smtpTls = new() { Text = "TLS (SSL)", AutoSize = true };
    private readonly MaterialTextBox2 _smtpUser = new() { Hint = L.CredentialDialog_User, Width = 360 };
    private readonly MaterialTextBox2 _smtpFrom = new() { Hint = L.ServerSettingsView_SenderFrom, Width = 360 };
    private readonly MaterialTextBox2 _smtpPass = new() { Hint = L.MainForm_Password, Width = 360, UseSystemPasswordChar = true };

    private readonly MaterialTextBox2 _graphTenant = new() { Hint = "Tenant ID", Width = 360 };
    private readonly MaterialTextBox2 _graphClient = new() { Hint = "Client (App) ID", Width = 360 };
    private readonly MaterialTextBox2 _graphSender = new() { Hint = L.ServerSettingsView_SenderMailboxUPNEmail, Width = 360 };
    private readonly MaterialTextBox2 _graphSecret = new() { Hint = "Client secret", Width = 360, UseSystemPasswordChar = true };
    private readonly DateTimePicker _graphExpiry = new() { Format = DateTimePickerFormat.Short, Width = 200 };
    private readonly ToolTip _tips = new() { IsBalloon = true, AutoPopDelay = 30000, InitialDelay = 250, ReshowDelay = 100 };

    private const string TenantUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/TenantOverview.ReactView/initialValue//tabId//recommendationResourceId//fromNav/Identity";
    private const string AppRegUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade/quickStartType~/null/isMSAApp~/false";

    private readonly MaterialTextBox2 _testTo = new() { Hint = L.ServerSettingsView_TestRecipient, Width = 280 };

    public ServerSettingsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _tabGeneral.Click += (_, _) => SelectTab("general");
        _tabEmail.Click += (_, _) => SelectTab("email");
        _tabServerUpdate.Click += (_, _) => SelectTab("update");
        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([_tabGeneral, _tabEmail, _tabServerUpdate]);

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();
        _saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 6) };
        _saveRow.Controls.Add(save);

        _provider.Items.AddRange([L.DeviceTelemetryPanel_No, "SMTP", "MS Graph (O365)"]);
        _provider.SelectedIndexChanged += (_, _) => ApplyProviderVisibility();

        _language.Items.AddRange(new object[] { new LangItem("auto", L.ServerSettingsView_LanguageAuto), new LangItem("en", "English"), new LangItem("hu", "Magyar") });
        _language.SelectedIndex = 0;

        BuildSmtpBox();
        BuildGraphBox();

        Controls.Add(ViewUi.Rows(1, tabbar, _tabContent, _saveRow, ViewUi.StatusHost(_status)));
    }

    public void ApplyTheme() => ThemeManager.StyleView(this);

    public async Task OnShownAsync()
    {
        await LoadAsync();
        SelectTab("general");
    }

    private static MaterialButton TabBtn(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(4, 0, 0, 0), Type = MaterialButton.MaterialButtonType.Text };

    private void BuildSmtpBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        void Add(Control c) { c.Margin = new Padding(4, 10, 0, 4); f.Controls.Add(c); }
        Add(_smtpHost);
        Add(_smtpPort);
        Add(_smtpTls);
        Add(_smtpUser);
        Add(_smtpFrom);
        Add(_smtpPass);
        _smtpBox.Dock = DockStyle.Top; _smtpBox.Controls.Add(f);
    }

    private void BuildGraphBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        void Lbl(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        void Help(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 2, 0, 0), ForeColor = Color.Gray });
        void Add(Control c) { c.Margin = new Padding(4, 10, 0, 4); f.Controls.Add(c); }

        // Fields keep their floating Hint and the info icons; only the redundant captions are gone.
        Add(HRow(_graphTenant, InfoIcon(L.ServerSettingsView_WhereDoIFindThe, TenantUrl)));
        Add(HRow(_graphClient, InfoIcon(
            L.ServerSettingsView_RegisterANewApplicationEntra +
            L.ServerSettingsView_SupportedAccountTypeSingleTenant +
            L.ServerSettingsView_CertificatesAndSecretsNewClient +
            L.ServerSettingsView_APIPermissionsMicrosoftGraphApplication +
            L.ServerSettingsView_IfNeededSendingCanBe +
            L.ServerSettingsView_ClickTheIconToOpen, AppRegUrl)));
        Add(_graphSender);
        Add(_graphSecret);
        // The expiry is a DateTimePicker (no floating hint), so it keeps its caption.
        Lbl(L.ServerSettingsView_SecretExpiryMax2Years);
        _graphExpiry.MinDate = DateTime.Today;
        _graphExpiry.MaxDate = DateTime.Today.AddYears(2);
        f.Controls.Add(_graphExpiry);
        Help(L.ServerSettingsView_X30DaysBeforeExpiryThe);

        _graphBox.Dock = DockStyle.Top; _graphBox.Controls.Add(f);
    }

    /// <summary>Small info icon (Segoe MDL2 Assets): tooltip help plus browser link on click.</summary>
    private Label InfoIcon(string tooltip, string url)
    {
        var l = new Label
        {
            Text = "", // Segoe MDL2 Assets: Info
            Font = new Font("Segoe MDL2 Assets", 13F),
            AutoSize = true,
            ForeColor = Color.DodgerBlue,
            Cursor = Cursors.Hand,
            Margin = new Padding(10, 16, 0, 0),
        };
        _tips.SetToolTip(l, tooltip);
        l.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ } };
        return l;
    }

    /// <summary>Info icon: balloon tooltip on hover, and a popup with the same text on click.</summary>
    private Label InfoTip(string info)
    {
        var l = new Label { Text = ((char)0xE946).ToString(), Font = new Font("Segoe MDL2 Assets", 13F), AutoSize = true, ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand, Margin = new Padding(10, 6, 0, 0) };
        _tips.SetToolTip(l, info);
        l.Click += (_, _) => MessageBox.Show(info, L.ServerSettingsView_MessageLanguage, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return l;
    }

    /// <summary>Horizontal row with textbox and icon side by side.</summary>
    private static FlowLayoutPanel HRow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        p.Controls.AddRange(cs);
        return p;
    }

    private void SelectTab(string tab)
    {
        _tabGeneral.Type = tab == "general" ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;
        _tabEmail.Type = tab == "email" ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;
        _tabServerUpdate.Type = tab == "update" ? MaterialButton.MaterialButtonType.Contained : MaterialButton.MaterialButtonType.Text;

        // The "Save" (settings) button is irrelevant on the server-update tab.
        _saveRow.Visible = tab != "update";

        _tabContent.Controls.Clear();
        _tabContent.Controls.Add(tab switch
        {
            "email" => BuildEmailTab(),
            "update" => BuildServerUpdateTab(),
            _ => BuildGeneralTab(),
        });
    }

    private Control BuildServerUpdateTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 8) };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        top.Controls.Add(_updVersion);
        top.Controls.Add(new MaterialLabel
        {
            Text = L.ServerSettingsView_ServerUpdateHelp, AutoSize = true, MaximumSize = new Size(760, 0),
            FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, ForeColor = Color.Gray, Margin = new Padding(4, 6, 0, 8),
        });

        var pickRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        var tarBtn = ViewUi.ToolbarButton(L.ServerSettingsView_SelectTar, primary: false);
        tarBtn.Click += async (_, _) => await UploadServerArtifactAsync("tar");
        var sqlBtn = ViewUi.ToolbarButton(L.ServerSettingsView_SelectSql, primary: false);
        sqlBtn.Click += async (_, _) => await UploadServerArtifactAsync("sql");
        var refreshBtn = ViewUi.ToolbarButton(L.AboutView_Refresh, primary: false);
        refreshBtn.Click += async (_, _) => await RefreshUpdateStatusAsync();
        pickRow.Controls.AddRange([tarBtn, sqlBtn, refreshBtn]);
        top.Controls.Add(pickRow);

        var actionRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        _updBtn = ViewUi.ToolbarButton(L.ServerSettingsView_UpdateServer);
        _updBtn.Click += async (_, _) => await DoServerUpdateAsync();
        _rbBtn = ViewUi.ToolbarButton(L.ServerSettingsView_Rollback, primary: false);
        _rbBtn.Click += async (_, _) => await DoServerRollbackAsync();
        actionRow.Controls.AddRange([_updBtn, _rbBtn]);
        top.Controls.Add(actionRow);

        _updResult.Dock = DockStyle.Fill;   // fills the rest of the tab, so it grows with the window
        _updResult.BackColor = ThemeManager.IsDark ? Color.FromArgb(45, 45, 48) : Color.White;
        _updResult.ForeColor = ThemeManager.IsDark ? Color.Gainsboro : Color.Black;

        root.Controls.Add(_updResult);   // Fill (added first)
        root.Controls.Add(top);          // Top (added after) -> header above, log fills the rest

        _ = RefreshUpdateStatusAsync();
        return root;
    }

    private async Task RefreshUpdateStatusAsync()
    {
        try
        {
            var s = await _api.GetServerUpdateStatusAsync();
            var head = L.Format(L.ServerSettingsView_CurrentServerVersion, s.Version) + "    "
                + L.Format(L.ServerSettingsView_Staged, s.StagedTar ? $"tar ({s.StagedTarSize / 1024 / 1024} MB)" : "—", s.StagedSql ? "sql" : "—");
            if (!s.HelperReady) head += "    " + L.ServerSettingsView_HelperMissing;
            _updVersion.Text = head;
            _updBtn.Enabled = s.HelperReady && s.StagedTar;       // need the helper installed + a staged package
            _rbBtn.Enabled = s.HelperReady && s.BackupAvailable;  // need the helper + a backup to restore
            _updResult.Text = s.LastResult is { } r ? (r.Ok ? "✓ " : "✗ ") + r.At + "\r\n" + Crlf(r.Message) : "";
        }
        catch (Exception ex) { _updResult.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    // The Linux server log uses LF line endings; a WinForms TextBox only breaks lines on CRLF.
    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private async Task UploadServerArtifactAsync(string kind)
    {
        using var d = new OpenFileDialog
        {
            Filter = kind == "tar"
                ? "Server package (*.tar.gz;*.tgz)|*.tar.gz;*.tgz|All files (*.*)|*.*"
                : "SQL (*.sql)|*.sql|All files (*.*)|*.*",
        };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Enabled = false;
            _status.Text = L.ServerSettingsView_Uploading;
            await _api.UploadServerPackageAsync(kind, d.FileName);
            _status.Text = L.ServerSettingsView_Uploaded;
            Enabled = true;
            await RefreshUpdateStatusAsync();
        }
        catch (Exception ex) { Enabled = true; _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task DoServerUpdateAsync()
    {
        if (MessageBox.Show(L.ServerSettingsView_UpdateConfirm, L.ServerSettingsView_ServerUpdate, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.TriggerServerUpdateAsync(); await PollResultAsync(); }
        catch (Exception ex) { _updResult.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task DoServerRollbackAsync()
    {
        if (MessageBox.Show(L.ServerSettingsView_RollbackConfirm, L.ServerSettingsView_Rollback, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RollbackServerAsync(); await PollResultAsync(); }
        catch (Exception ex) { _updResult.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    /// <summary>Polls status across the server restart gap until a fresh result appears (or times out).</summary>
    private async Task PollResultAsync()
    {
        _updResult.Text = L.ServerSettingsView_UpdateInProgress;
        for (int i = 0; i < 60; i++) // ~3 minutes at 3s
        {
            await Task.Delay(3000);
            try
            {
                var s = await _api.GetServerUpdateStatusAsync();
                if (s.LastResult is { } r)
                {
                    _updVersion.Text = L.Format(L.ServerSettingsView_CurrentServerVersion, s.Version);
                    _updResult.Text = (r.Ok ? "✓ " : "✗ ") + r.At + "\r\n" + Crlf(r.Message);
                    return;
                }
            }
            catch { /* server restarting; keep waiting */ }
        }
        _updResult.Text = L.ServerSettingsView_UpdateTimeout;
    }

    private Control BuildGeneralTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 12, 12, 8) };
        // No captions above the fields: each control's floating Hint already labels it.
        void Add(Control c) { c.Margin = new Padding(4, 10, 0, 4); body.Controls.Add(c); }
        Add(_owner);
        Add(_phone);
        Add(_email);
        Add(HRow(_language, InfoTip(L.ServerSettingsView_MessageLanguageInfo)));
        return body;
    }

    private Control BuildEmailTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 12, 12, 8) };
        _provider.Margin = new Padding(4, 10, 0, 4);
        body.Controls.Add(_provider);
        body.Controls.Add(_smtpBox);
        body.Controls.Add(_graphBox);

        var testLbl = new MaterialLabel { Text = L.ServerSettingsView_TestDeliverySaveBeforeTesting, Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 16, 0, 4) };
        body.Controls.Add(testLbl);
        var testRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        var testBtn = ViewUi.ToolbarButton(L.ServerSettingsView_SendTestEmail, primary: false);
        testBtn.Click += async (_, _) => await TestAsync();
        testRow.Controls.Add(_testTo);
        testRow.Controls.Add(testBtn);
        body.Controls.Add(testRow);

        ApplyProviderVisibility();
        return body;
    }

    private void ApplyProviderVisibility()
    {
        _smtpBox.Visible = _provider.SelectedIndex == 1;
        _graphBox.Visible = _provider.SelectedIndex == 2;
    }

    private async Task LoadAsync()
    {
        try
        {
            _status.Text = L.ServerSettingsView_FetchingSettings;
            var s = await _api.GetSettingsAsync();

            _owner.Text = s.OwnerName ?? "";
            _phone.Text = s.SupportPhone ?? "";
            _email.Text = s.SupportEmail ?? "";

            var langCode = string.IsNullOrWhiteSpace(s.Language) ? "auto" : s.Language;
            _language.SelectedIndex = 0;
            for (int i = 0; i < _language.Items.Count; i++)
                if (_language.Items[i] is LangItem li && li.Code == langCode) { _language.SelectedIndex = i; break; }

            _provider.SelectedIndex = s.EmailProvider switch { "smtp" => 1, "graph" => 2, _ => 0 };
            _smtpHost.Text = s.SmtpHost ?? "";
            _smtpPort.Text = s.SmtpPort.ToString();
            _smtpTls.Checked = s.SmtpUseTls;
            _smtpUser.Text = s.SmtpUser ?? "";
            _smtpFrom.Text = s.SmtpFrom ?? "";
            _smtpPass.Text = ""; _smtpPass.Hint = s.HasSmtpPassword ? L.ServerSettingsView_PasswordSetLeaveEmpty : L.MainForm_Password;

            _graphTenant.Text = s.GraphTenantId ?? "";
            _graphClient.Text = s.GraphClientId ?? "";
            _graphSender.Text = s.GraphSender ?? "";
            _graphSecret.Text = ""; _graphSecret.Hint = s.HasGraphSecret ? L.ServerSettingsView_ClientSecretSetLeaveEmpty : "Client secret";

            // Required expiry: show saved value when present (clamped), otherwise default to 2 years.
            var d = s.GraphSecretExpiresAt?.LocalDateTime.Date ?? _graphExpiry.MaxDate;
            _graphExpiry.Value = d < _graphExpiry.MinDate ? _graphExpiry.MinDate : (d > _graphExpiry.MaxDate ? _graphExpiry.MaxDate : d);

            _status.Text = ""; // settings loaded; the filled form is the confirmation, no status text needed
        }
        catch (Exception ex) { _status.Text = L.ServerSettingsView_FetchError + ex.Message; }
    }

    private async Task SaveAsync()
    {
        try
        {
            var info = new ServerSettingsInfo
            {
                OwnerName = _owner.Text.Trim(),
                SupportPhone = _phone.Text.Trim(),
                SupportEmail = _email.Text.Trim(),
                Language = (_language.SelectedItem as LangItem)?.Code ?? "auto",
                EmailProvider = _provider.SelectedIndex switch { 1 => "smtp", 2 => "graph", _ => "none" },
                SmtpHost = _smtpHost.Text.Trim(),
                SmtpPort = int.TryParse(_smtpPort.Text.Trim(), out var p) ? p : 587,
                SmtpUseTls = _smtpTls.Checked,
                SmtpUser = _smtpUser.Text.Trim(),
                SmtpFrom = _smtpFrom.Text.Trim(),
                SmtpPassword = _smtpPass.Text,       // empty = unchanged, handled by server
                GraphTenantId = _graphTenant.Text.Trim(),
                GraphClientId = _graphClient.Text.Trim(),
                GraphSender = _graphSender.Text.Trim(),
                GraphClientSecret = _graphSecret.Text, // empty = unchanged
                GraphSecretExpiresAt = new DateTimeOffset(_graphExpiry.Value.Date), // required
            };
            await _api.UpdateSettingsAsync(info);
            _status.Text = "Mentve.";
            await LoadAsync(); // refresh Has* placeholders
        }
        catch (Exception ex) { _status.Text = L.ServerSettingsView_SaveError + ex.Message; }
    }

    private async Task TestAsync()
    {
        var to = _testTo.Text.Trim();
        if (string.IsNullOrWhiteSpace(to)) { _status.Text = L.ServerSettingsView_EnterATestRecipient; return; }
        _status.Text = L.ServerSettingsView_SendingTestEmail;
        var (ok, err) = await _api.TestEmailAsync(to);
        _status.Text = ok ? L.Format(L.ServerSettingsView_TestEmailSent, to) : L.ServerSettingsView_TestError + err;
    }
}
