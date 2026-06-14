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
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private readonly MaterialLabel _status = new();

    // General
    private readonly MaterialTextBox2 _owner = new() { Hint = L.ServerSettingsView_OwnerName, Width = 360 };
    private readonly MaterialTextBox2 _phone = new() { Hint = L.ServerSettingsView_SupportPhoneNumber, Width = 360 };
    private readonly MaterialTextBox2 _email = new() { Hint = L.ServerSettingsView_SupportEmail, Width = 360 };

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
        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([_tabGeneral, _tabEmail]);

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();
        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 6) };
        saveRow.Controls.Add(save);

        _provider.Items.AddRange([L.DeviceTelemetryPanel_No, "SMTP", "MS Graph (O365)"]);
        _provider.SelectedIndexChanged += (_, _) => ApplyProviderVisibility();

        BuildSmtpBox();
        BuildGraphBox();

        Controls.Add(ViewUi.Rows(1, tabbar, _tabContent, saveRow, ViewUi.StatusHost(_status)));
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
        void Lbl(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl(L.ServerSettingsView_SMTPServer); f.Controls.Add(_smtpHost);
        Lbl("Port"); f.Controls.Add(_smtpPort);
        f.Controls.Add(_smtpTls);
        Lbl(L.CredentialDialog_User); f.Controls.Add(_smtpUser);
        Lbl(L.ServerSettingsView_SenderFrom); f.Controls.Add(_smtpFrom);
        Lbl(L.MainForm_Password); f.Controls.Add(_smtpPass);
        _smtpBox.Dock = DockStyle.Top; _smtpBox.Controls.Add(f);
    }

    private void BuildGraphBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        void Lbl(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        void Help(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 2, 0, 0), ForeColor = Color.Gray });

        Lbl("Azure Tenant ID");
        f.Controls.Add(HRow(_graphTenant, InfoIcon(
            L.ServerSettingsView_WhereDoIFindThe, TenantUrl)));

        Lbl("Client (App) ID");
        f.Controls.Add(HRow(_graphClient, InfoIcon(
            L.ServerSettingsView_RegisterANewApplicationEntra +
            L.ServerSettingsView_SupportedAccountTypeSingleTenant +
            L.ServerSettingsView_CertificatesAndSecretsNewClient +
            L.ServerSettingsView_APIPermissionsMicrosoftGraphApplication +
            L.ServerSettingsView_IfNeededSendingCanBe +
            L.ServerSettingsView_ClickTheIconToOpen, AppRegUrl)));

        Lbl(L.ServerSettingsView_SenderMailboxUPNEmail); f.Controls.Add(_graphSender);
        Lbl("Client secret"); f.Controls.Add(_graphSecret);
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

        _tabContent.Controls.Clear();
        _tabContent.Controls.Add(tab == "email" ? BuildEmailTab() : BuildGeneralTab());
    }

    private Control BuildGeneralTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        Lbl(L.ServerSettingsView_OwnerName); body.Controls.Add(_owner);
        Lbl(L.ServerSettingsView_SupportPhoneNumber); body.Controls.Add(_phone);
        Lbl(L.ServerSettingsView_SupportEmail); body.Controls.Add(_email);
        return body;
    }

    private Control BuildEmailTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        body.Controls.Add(new MaterialLabel { Text = L.ServerSettingsView_ActiveProvider, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 4, 0, 0) });
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

            _status.Text = L.AboutView_Upd;
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
