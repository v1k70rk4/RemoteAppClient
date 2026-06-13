using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>
/// Szerver-szintű beállítások (admin): „Általános" fül (tulajdonos + support) és
/// „E-mail küldés" fül (SMTP vagy MS Graph app-only) + teszt-küldés. Lent: Mentés.
/// </summary>
public sealed class ServerSettingsView : UserControl, IContentView
{
    private readonly AdminApi _api;

    private readonly MaterialButton _tabGeneral = TabBtn("Általános");
    private readonly MaterialButton _tabEmail = TabBtn("E-mail küldés");
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private readonly MaterialLabel _status = new();

    // Általános
    private readonly MaterialTextBox2 _owner = new() { Hint = "Tulajdonos neve", Width = 360 };
    private readonly MaterialTextBox2 _phone = new() { Hint = "Támogatási telefonszám", Width = 360 };
    private readonly MaterialTextBox2 _email = new() { Hint = "Támogatási e-mail", Width = 360 };

    // E-mail provider + mezők
    private readonly MaterialComboBox _provider = new() { Hint = "E-mail provider", Width = 260 };
    private readonly Panel _smtpBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel _graphBox = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    private readonly MaterialTextBox2 _smtpHost = new() { Hint = "SMTP host", Width = 360 };
    private readonly MaterialTextBox2 _smtpPort = new() { Hint = "Port", Width = 120 };
    private readonly MaterialSwitch _smtpTls = new() { Text = "TLS (SSL)", AutoSize = true };
    private readonly MaterialTextBox2 _smtpUser = new() { Hint = "Felhasználó", Width = 360 };
    private readonly MaterialTextBox2 _smtpFrom = new() { Hint = "Feladó (From)", Width = 360 };
    private readonly MaterialTextBox2 _smtpPass = new() { Hint = "Jelszó", Width = 360, UseSystemPasswordChar = true };

    private readonly MaterialTextBox2 _graphTenant = new() { Hint = "Tenant ID", Width = 360 };
    private readonly MaterialTextBox2 _graphClient = new() { Hint = "Client (App) ID", Width = 360 };
    private readonly MaterialTextBox2 _graphSender = new() { Hint = "Feladó postafiók (UPN/e-mail)", Width = 360 };
    private readonly MaterialTextBox2 _graphSecret = new() { Hint = "Client secret", Width = 360, UseSystemPasswordChar = true };
    private readonly DateTimePicker _graphExpiry = new() { Format = DateTimePickerFormat.Short, Width = 200 };
    private readonly ToolTip _tips = new() { IsBalloon = true, AutoPopDelay = 30000, InitialDelay = 250, ReshowDelay = 100 };

    private const string TenantUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_IAM/TenantOverview.ReactView/initialValue//tabId//recommendationResourceId//fromNav/Identity";
    private const string AppRegUrl = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/CreateApplicationBlade/quickStartType~/null/isMSAApp~/false";

    private readonly MaterialTextBox2 _testTo = new() { Hint = "Teszt címzett", Width = 280 };

    public ServerSettingsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _tabGeneral.Click += (_, _) => SelectTab("general");
        _tabEmail.Click += (_, _) => SelectTab("email");
        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([_tabGeneral, _tabEmail]);

        var save = ViewUi.ToolbarButton("Mentés");
        save.Click += async (_, _) => await SaveAsync();
        var saveRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 6) };
        saveRow.Controls.Add(save);

        _provider.Items.AddRange(["nincs", "SMTP", "MS Graph (O365)"]);
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
        Lbl("SMTP szerver"); f.Controls.Add(_smtpHost);
        Lbl("Port"); f.Controls.Add(_smtpPort);
        f.Controls.Add(_smtpTls);
        Lbl("Felhasználó"); f.Controls.Add(_smtpUser);
        Lbl("Feladó (From)"); f.Controls.Add(_smtpFrom);
        Lbl("Jelszó"); f.Controls.Add(_smtpPass);
        _smtpBox.Dock = DockStyle.Top; _smtpBox.Controls.Add(f);
    }

    private void BuildGraphBox()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        void Lbl(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 10, 0, 0) });
        void Help(string t) => f.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 2, 0, 0), ForeColor = Color.Gray });

        Lbl("Azure Tenant ID");
        f.Controls.Add(HRow(_graphTenant, InfoIcon(
            "Hol találom a Tenant ID-t?\nEntra → Áttekintés (Identity → Overview).\n\nKattints az ikonra a megnyitáshoz.", TenantUrl)));

        Lbl("Client (App) ID");
        f.Controls.Add(HRow(_graphClient, InfoIcon(
            "Új alkalmazás regisztrálása (Entra → App registrations).\n\n" +
            "• Támogatott fióktípus: „Csak ez a szervezeti címtár (egybérlős)”.\n" +
            "• Tanúsítványok és titkos kulcsok → Új titkos kulcs (client secret).\n" +
            "• API-engedélyek → Microsoft Graph → Alkalmazás engedélyek → Mail.Send → Rendszergazdai jóváhagyás.\n" +
            "• Igény esetén a küldés egyetlen postafiókra korlátozható (Application Access Policy).\n\n" +
            "Kattints az ikonra a megnyitáshoz.", AppRegUrl)));

        Lbl("Feladó postafiók (UPN/e-mail)"); f.Controls.Add(_graphSender);
        Lbl("Client secret"); f.Controls.Add(_graphSecret);
        Lbl("Secret lejárata (max 2 év, kötelező)");
        _graphExpiry.MinDate = DateTime.Today;
        _graphExpiry.MaxDate = DateTime.Today.AddYears(2);
        f.Controls.Add(_graphExpiry);
        Help("30 nappal a lejárat előtt a szerver e-mailt küld a támogatási címre, és a kliens is jelez.");

        _graphBox.Dock = DockStyle.Top; _graphBox.Controls.Add(f);
    }

    /// <summary>Kis info-ikon (Segoe MDL2 Assets): buborék-tooltip (súgó) + kattintásra böngészőben nyit.</summary>
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
        l.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* nincs böngésző */ } };
        return l;
    }

    /// <summary>Vízszintes sor (szövegdoboz + ikon egymás mellett).</summary>
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
        Lbl("Tulajdonos neve"); body.Controls.Add(_owner);
        Lbl("Támogatási telefonszám"); body.Controls.Add(_phone);
        Lbl("Támogatási e-mail"); body.Controls.Add(_email);
        return body;
    }

    private Control BuildEmailTab()
    {
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        body.Controls.Add(new MaterialLabel { Text = "Aktív provider", FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, Margin = new Padding(4, 4, 0, 0) });
        body.Controls.Add(_provider);
        body.Controls.Add(_smtpBox);
        body.Controls.Add(_graphBox);

        var testLbl = new MaterialLabel { Text = "Teszt küldés (a tesztelés előtt mentsen!)", Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 16, 0, 4) };
        body.Controls.Add(testLbl);
        var testRow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        var testBtn = ViewUi.ToolbarButton("Teszt e-mail küldése", primary: false);
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
            _status.Text = "Beállítások lekérése…";
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
            _smtpPass.Text = ""; _smtpPass.Hint = s.HasSmtpPassword ? "Jelszó (beállítva — üresen marad)" : "Jelszó";

            _graphTenant.Text = s.GraphTenantId ?? "";
            _graphClient.Text = s.GraphClientId ?? "";
            _graphSender.Text = s.GraphSender ?? "";
            _graphSecret.Text = ""; _graphSecret.Hint = s.HasGraphSecret ? "Client secret (beállítva — üresen marad)" : "Client secret";

            // Kötelező lejárat: ha van mentett, azt mutatjuk (clamp), különben default 2 év.
            var d = s.GraphSecretExpiresAt?.LocalDateTime.Date ?? _graphExpiry.MaxDate;
            _graphExpiry.Value = d < _graphExpiry.MinDate ? _graphExpiry.MinDate : (d > _graphExpiry.MaxDate ? _graphExpiry.MaxDate : d);

            _status.Text = "Friss.";
        }
        catch (Exception ex) { _status.Text = "Lekérés hiba: " + ex.Message; }
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
                SmtpPassword = _smtpPass.Text,       // üres = változatlan (szerver kezeli)
                GraphTenantId = _graphTenant.Text.Trim(),
                GraphClientId = _graphClient.Text.Trim(),
                GraphSender = _graphSender.Text.Trim(),
                GraphClientSecret = _graphSecret.Text, // üres = változatlan
                GraphSecretExpiresAt = new DateTimeOffset(_graphExpiry.Value.Date), // kötelező
            };
            await _api.UpdateSettingsAsync(info);
            _status.Text = "Mentve.";
            await LoadAsync(); // frissíti a Has* helykitöltőket
        }
        catch (Exception ex) { _status.Text = "Mentés hiba: " + ex.Message; }
    }

    private async Task TestAsync()
    {
        var to = _testTo.Text.Trim();
        if (string.IsNullOrWhiteSpace(to)) { _status.Text = "Adj meg teszt-címzettet."; return; }
        _status.Text = "Teszt e-mail küldése…";
        var (ok, err) = await _api.TestEmailAsync(to);
        _status.Text = ok ? $"Teszt e-mail elküldve: {to}" : "Teszt hiba: " + err;
    }
}
