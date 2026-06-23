using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Server-level admin settings: TabStrip (General / Email delivery / Server update) over cards. Text inputs
/// are owner-drawn <see cref="TextField"/>s with a caption above each; provider/language stay MaterialSkin
/// combos. The server-update tab has a status card + a dark console. See design_handoff_console_redesign.
/// </summary>
public sealed class ServerSettingsView : UserControl, IContentView
{
    private readonly AdminApi _api;

    private readonly TabStrip _tabs = new();
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 8, 22, 8) };
    private Panel _saveRow = null!;
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    // Server update tab
    private readonly MaterialLabel _updVersion = new() { AutoSize = true, Margin = new Padding(2, 4, 0, 4) };
    private readonly TextBox _updResult = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9.5F) };
    private UiButton _updBtn = null!;
    private UiButton _rbBtn = null!;

    // General
    private readonly TextField _owner = new("", 440);
    private readonly TextField _phone = new("", 440, mono: true);
    private readonly TextField _email = new("", 440, mono: true);
    private readonly UiCombo _language = new(280);

    private sealed record LangItem(string Code, string Name) { public override string ToString() => Name; }

    // Email provider + fields
    private readonly UiCombo _provider = new(280);
    private readonly Panel _smtpBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel _graphBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private readonly TextField _smtpHost = new("", 440, mono: true);
    private readonly TextField _smtpPort = new("", 120, mono: true);
    private readonly UiToggle _smtpTls = new("TLS (SSL)");
    private readonly TextField _smtpUser = new("", 440, mono: true);
    private readonly TextField _smtpFrom = new("", 440, mono: true);
    private readonly TextField _smtpPass = new("", 440, password: true);

    private readonly TextField _graphTenant = new("", 440, mono: true);
    private readonly TextField _graphClient = new("", 440, mono: true);
    private readonly TextField _graphSender = new("", 440, mono: true);
    private readonly TextField _graphSecret = new("", 440, password: true);
    private readonly DateTimePicker _graphExpiry = new() { Format = DateTimePickerFormat.Short, Width = 200 };
    private readonly ToolTip _tips = new() { IsBalloon = true, AutoPopDelay = 30000, InitialDelay = 250, ReshowDelay = 100 };

    private const string TenantUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/TenantOverview.ReactView/initialValue//tabId//recommendationResourceId//fromNav/Identity";
    private const string AppRegUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade/quickStartType~/null/isMSAApp~/false";

    private readonly TextField _testTo = new("", 300);

    public ServerSettingsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        _tabs.SetTabs(new[]
        {
            ("general", L.ChannelsView_General), ("email", L.ServerSettingsView_EmailDelivery), ("update", L.ServerSettingsView_ServerUpdate),
        }, "general");
        _tabs.TabSelected += SelectTab;

        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();
        _saveRow = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = ThemeManager.Bg, Padding = new Padding(22, 6, 22, 6) };
        save.Location = new Point(22, 6);
        _saveRow.Controls.Add(save);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg, Padding = new Padding(24, 0, 0, 0) };
        statusHost.Controls.Add(_status);

        _provider.Items.AddRange([L.DeviceTelemetryPanel_No, "SMTP", "MS Graph (O365)"]);
        _provider.SelectedIndexChanged += (_, _) => ApplyProviderVisibility();
        _language.Items.AddRange(new object[] { new LangItem("auto", L.ServerSettingsView_LanguageAuto), new LangItem("en", "English"), new LangItem("hu", "Magyar") });
        _language.SelectedIndex = 0;

        BuildSmtpBox();
        BuildGraphBox();

        Controls.Add(_tabContent);
        Controls.Add(_saveRow);
        Controls.Add(statusHost);
        Controls.Add(_tabs);
    }

    public void ApplyTheme() { BackColor = ThemeManager.Bg; Invalidate(true); }

    public async Task OnShownAsync()
    {
        await LoadAsync();
        SelectTab("general");
    }

    /// <summary>Adds a caption label then the field to a top-down flow.</summary>
    private static void AddField(FlowLayoutPanel f, string caption, Control field)
    {
        f.Controls.Add(new MaterialLabel { Text = caption, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, ForeColor = ThemeManager.Text3, Margin = new Padding(4, 12, 0, 1) });
        field.Margin = new Padding(4, 0, 0, 2);
        f.Controls.Add(field);
    }

    private void BuildSmtpBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        AddField(f, "SMTP host", _smtpHost);
        AddField(f, "Port", _smtpPort);
        _smtpTls.Margin = new Padding(4, 12, 0, 4);
        f.Controls.Add(_smtpTls);
        AddField(f, L.CredentialDialog_User, _smtpUser);
        AddField(f, L.ServerSettingsView_SenderFrom, _smtpFrom);
        AddField(f, L.MainForm_Password, _smtpPass);
        _smtpBox.Dock = DockStyle.Top; _smtpBox.Controls.Add(f);
    }

    private void BuildGraphBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        void Help(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 2, 0, 0), ForeColor = ThemeManager.WarnFg });

        AddField(f, "Tenant ID", HRow(_graphTenant, InfoIcon(L.ServerSettingsView_WhereDoIFindThe, TenantUrl)));
        AddField(f, "Client (App) ID", HRow(_graphClient, InfoIcon(
            L.ServerSettingsView_RegisterANewApplicationEntra +
            L.ServerSettingsView_SupportedAccountTypeSingleTenant +
            L.ServerSettingsView_CertificatesAndSecretsNewClient +
            L.ServerSettingsView_APIPermissionsMicrosoftGraphApplication +
            L.ServerSettingsView_IfNeededSendingCanBe +
            L.ServerSettingsView_ClickTheIconToOpen, AppRegUrl)));
        AddField(f, L.ServerSettingsView_SenderMailboxUPNEmail, _graphSender);
        AddField(f, "Client secret", _graphSecret);
        _graphExpiry.MinDate = DateTime.Today;
        _graphExpiry.MaxDate = DateTime.Today.AddYears(2);
        AddField(f, L.ServerSettingsView_SecretExpiryMax2Years, _graphExpiry);
        Help(L.ServerSettingsView_X30DaysBeforeExpiryThe);

        _graphBox.Dock = DockStyle.Top; _graphBox.Controls.Add(f);
    }

    /// <summary>Small info icon (Segoe MDL2 Assets): tooltip help plus browser link on click.</summary>
    private Label InfoIcon(string tooltip, string url)
    {
        var l = new Label { Text = "", Font = new Font("Segoe MDL2 Assets", 13F), AutoSize = true, ForeColor = ThemeManager.Accent, Cursor = Cursors.Hand, Margin = new Padding(10, 9, 0, 0) };
        _tips.SetToolTip(l, tooltip);
        l.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ } };
        return l;
    }

    private Label InfoTip(string info)
    {
        var l = new Label { Text = ((char)0xE946).ToString(), Font = new Font("Segoe MDL2 Assets", 13F), AutoSize = true, ForeColor = ThemeManager.Accent, Cursor = Cursors.Hand, Margin = new Padding(10, 9, 0, 0) };
        _tips.SetToolTip(l, info);
        l.Click += (_, _) => MessageBox.Show(info, L.ServerSettingsView_MessageLanguage, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return l;
    }

    private static FlowLayoutPanel HRow(params Control[] cs)
    {
        var p = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
        p.Controls.AddRange(cs);
        return p;
    }

    private void SelectTab(string tab)
    {
        _tabs.SetActive(tab);
        _saveRow.Visible = tab != "update";
        _tabContent.Controls.Clear();
        _tabContent.Controls.Add(tab switch
        {
            "email" => BuildEmailTab(),
            "update" => BuildServerUpdateTab(),
            _ => BuildGeneralTab(),
        });
    }

    private Control BuildGeneralTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18, 14, 18, 12) };
        AddField(body, L.ServerSettingsView_OwnerName, _owner);
        AddField(body, L.ServerSettingsView_SupportPhoneNumber, _phone);
        AddField(body, L.ServerSettingsView_SupportEmail, _email);
        AddField(body, L.ServerSettingsView_MessageLanguage, HRow(_language, InfoTip(L.ServerSettingsView_MessageLanguageInfo)));
        return new CardPanel("", body);
    }

    private Control BuildEmailTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(18, 14, 18, 12) };
        AddField(body, "E-mail provider", _provider);
        body.Controls.Add(_smtpBox);
        body.Controls.Add(_graphBox);

        body.Controls.Add(new MaterialLabel { Text = L.ServerSettingsView_TestDeliverySaveBeforeTesting, Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 18, 0, 6) });
        var testBtn = new UiButton(L.ServerSettingsView_SendTestEmail, UiButton.Style.Outline) { Margin = new Padding(10, 0, 0, 0) };
        testBtn.Click += async (_, _) => await TestAsync();
        body.Controls.Add(HRow(_testTo, testBtn));

        ApplyProviderVisibility();
        return new CardPanel("", body);
    }

    private Control BuildServerUpdateTab()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Bg, Padding = new Padding(18, 8, 18, 10) };

        var top = new Panel { Dock = DockStyle.Top, Height = 200, BackColor = ThemeManager.Bg };
        var card = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Panel, Padding = new Padding(18, 14, 18, 14) };
        card.Paint += (_, e) => UiPaint.DrawCard(e.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        var inner = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = ThemeManager.Panel };
        inner.Controls.Add(_updVersion);
        var help = new MaterialLabel { Text = L.ServerSettingsView_ServerUpdateHelp, AutoSize = true, MaximumSize = new Size(800, 0), FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, ForeColor = ThemeManager.Text3, Margin = new Padding(2, 6, 0, 10) };
        inner.Controls.Add(help);
        // Wrap the help text at the card's inner width so long copy never spills past the card edge (esp. high DPI).
        inner.SizeChanged += (_, _) => { if (inner.ClientSize.Width > 16) help.MaximumSize = new Size(inner.ClientSize.Width - 8, 0); };

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = new Padding(0) };
        var tarBtn = new UiButton(L.ServerSettingsView_SelectTar, UiButton.Style.Outline) { Margin = new Padding(0, 0, 8, 0) };
        tarBtn.Click += async (_, _) => await UploadServerArtifactAsync("tar");
        var sqlBtn = new UiButton(L.ServerSettingsView_SelectSql, UiButton.Style.Outline) { Margin = new Padding(0, 0, 8, 0) };
        sqlBtn.Click += async (_, _) => await UploadServerArtifactAsync("sql");
        var refreshBtn = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline) { Margin = new Padding(0, 0, 8, 0) };
        refreshBtn.Click += async (_, _) => await RefreshUpdateStatusAsync();
        _updBtn = new UiButton(L.ServerSettingsView_UpdateServer) { Margin = new Padding(0, 0, 8, 0) };
        _updBtn.Click += async (_, _) => await DoServerUpdateAsync();
        _rbBtn = new UiButton(L.ServerSettingsView_Rollback, UiButton.Style.Warn);
        _rbBtn.Click += async (_, _) => await DoServerRollbackAsync();
        buttons.Controls.AddRange([tarBtn, sqlBtn, refreshBtn, _updBtn, _rbBtn]);
        inner.Controls.Add(buttons);

        card.Controls.Add(inner);
        top.Controls.Add(card);

        var console = new Panel { Dock = DockStyle.Fill, BackColor = ColorTranslator.FromHtml("#0a0f16"), Padding = new Padding(14, 12, 14, 12) };
        _updResult.Dock = DockStyle.Fill;
        _updResult.BackColor = ColorTranslator.FromHtml("#0a0f16");
        _updResult.ForeColor = ColorTranslator.FromHtml("#9bd6a8");
        console.Controls.Add(_updResult);
        var consoleHost = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Bg, Padding = new Padding(0, 12, 0, 0) };
        consoleHost.Controls.Add(console);

        root.Controls.Add(consoleHost);
        root.Controls.Add(top);

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
            _updBtn.Enabled = s.HelperReady && s.StagedTar;
            _rbBtn.Enabled = s.HelperReady && s.BackupAvailable;
            _updResult.Text = s.LastResult is { } r ? (r.Ok ? "✓ " : "✗ ") + r.At + "\r\n" + Crlf(r.Message) : "";
        }
        catch (Exception ex) { _updResult.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private async Task UploadServerArtifactAsync(string kind)
    {
        using var d = new OpenFileDialog
        {
            Filter = kind == "tar" ? "Server package (*.tar.gz;*.tgz)|*.tar.gz;*.tgz|All files (*.*)|*.*" : "SQL (*.sql)|*.sql|All files (*.*)|*.*",
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

    private async Task PollResultAsync()
    {
        _updResult.Text = L.ServerSettingsView_UpdateInProgress;
        for (int i = 0; i < 60; i++)
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
            _smtpPass.Text = ""; _smtpPass.Placeholder = s.HasSmtpPassword ? L.ServerSettingsView_PasswordSetLeaveEmpty : L.MainForm_Password;

            _graphTenant.Text = s.GraphTenantId ?? "";
            _graphClient.Text = s.GraphClientId ?? "";
            _graphSender.Text = s.GraphSender ?? "";
            _graphSecret.Text = ""; _graphSecret.Placeholder = s.HasGraphSecret ? L.ServerSettingsView_ClientSecretSetLeaveEmpty : "Client secret";

            var d = s.GraphSecretExpiresAt?.LocalDateTime.Date ?? _graphExpiry.MaxDate;
            _graphExpiry.Value = d < _graphExpiry.MinDate ? _graphExpiry.MinDate : (d > _graphExpiry.MaxDate ? _graphExpiry.MaxDate : d);

            _status.Text = "";
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
                SmtpPassword = _smtpPass.Text,
                GraphTenantId = _graphTenant.Text.Trim(),
                GraphClientId = _graphClient.Text.Trim(),
                GraphSender = _graphSender.Text.Trim(),
                GraphClientSecret = _graphSecret.Text,
                GraphSecretExpiresAt = new DateTimeOffset(_graphExpiry.Value.Date),
            };
            await _api.UpdateSettingsAsync(info);
            _status.Text = L.Common_Saved;
            await LoadAsync();
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
