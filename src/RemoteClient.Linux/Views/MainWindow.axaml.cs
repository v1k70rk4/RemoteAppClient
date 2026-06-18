using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Linux.Views;

public partial class MainWindow : Window
{
    private readonly ClientConfig _cfg = ClientConfig.Load();
    private LinuxOperatorTransport? _transport;
    private AdminApi? _api;
    private List<DeviceInfo> _devices = [];
    private DataGridCollectionView? _view;

    public MainWindow()
    {
        InitializeComponent();
        var asm = Assembly.GetEntryAssembly();
        var v = asm?.GetName().Version;
        var vs = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";
        Title = $"Multiserver Linux RemoteAppClient Lite  {vs}";
        SubtitleText.Text = $"Multiserver Linux · Lite · {vs}";
        ServerBox.Text = _cfg.LastServerUrl ?? "";
        UserBox.Text = _cfg.LastUsername ?? "";

        // Localize the chrome from the shared Core Strings (the same keys the Windows client uses). The UI
        // language is bound once at startup (App); Settings offers a switch that takes effect on restart.
        UserBox.Watermark = L.ForgotPasswordForm_Username;
        PassBox.Watermark = L.MainForm_Password;
        TotpBox.Watermark = L.MainForm_TOTPIfAny;
        RememberCheck.Content = L.MainForm_RememberDevice;
        LoginBtn.Content = L.MainForm_SignIn;
        ForgotBtn.Content = L.ForgotPasswordForm_PasswordRecovery;
        SearchBox.Watermark = L.DevicesView_SearchHostnameOrNote;
        ConnectBtn.Content = L.DevicesView_Connect;
        RefreshBtn.Content = L.AboutView_Refresh;
        SettingsBtn.Content = L.MainForm_Settings;
        LangLabel.Text = L.SettingsView_Language;
        ((ComboBoxItem)LangBox.Items[0]!).Content = L.SettingsView_AutoSystemLanguage;

        // Device-table column headers (localized; shared Core Strings - same labels as the detail panel).
        DeviceList.Columns[0].Header = L.DevicesView_Device;
        DeviceList.Columns[1].Header = L.BootstrapView_Group;
        DeviceList.Columns[2].Header = L.DeviceGeneralPanel_Note;
        DeviceList.Columns[3].Header = "Online";
        DeviceList.Columns[4].Header = L.DevicesView_LastOnline;

        // Password-recovery panel reuses the Windows ForgotPasswordForm labels (shared in Core).
        RecTitle.Text = L.ForgotPasswordForm_PasswordRecovery;
        RecStep1.Text = L.ForgotPasswordForm_X1EnterYourUsernameAnd;
        RecUserBox.Watermark = L.ForgotPasswordForm_Username;
        RecEmailBox.Watermark = L.ForgotPasswordForm_EmailAddress;
        RecRequestBtn.Content = L.ForgotPasswordForm_RequestToken;
        RecStep2.Text = L.ForgotPasswordForm_X2EnterTheTokenYou;
        RecPassBox.Watermark = L.ForgotPasswordForm_NewPasswordMin10;
        RecSetBtn.Content = L.ForgotPasswordForm_SetNewPassword;

        UpdateTotpVisibility();
        _ = CheckForUpdateAsync();
    }

    private async void OnLogin(object? sender, RoutedEventArgs e)
    {
        LoginBtn.IsEnabled = false;
        LoginStatus.Text = L.MainForm_Connecting;
        var server = ServerBox.Text?.Trim() ?? "";
        var user = UserBox.Text?.Trim() ?? "";
        try
        {
            // Reuse a saved 90-day TOTP trust token for this user (skips TOTP while it is still valid).
            var trust = string.Equals(_cfg.TrustUsername, user, StringComparison.OrdinalIgnoreCase) ? _cfg.TrustToken : null;
            var (transport, login) = await LinuxOperatorTransport.LoginAsync(
                server, user, PassBox.Text ?? "",
                string.IsNullOrWhiteSpace(TotpBox.Text) ? null : TotpBox.Text!.Trim(),
                trustToken: trust, rememberDevice: RememberCheck.IsChecked == true);

            if (login.MustChangePassword || login.TotpEnrollRequired)
            {
                transport.Dispose();
                LoginStatus.Text = "Finish first-time setup (password + TOTP) on the Windows console first.";
                return;
            }

            _transport = transport;
            _api = new AdminApi(ct => _transport!.ForwardAsync(5000, ct));
            _api.SetToken(login.Token);

            // Remember the server + user, and store the new 90-day trust token if the server issued one.
            _cfg.LastServerUrl = server;
            _cfg.LastUsername = user;
            if (!string.IsNullOrWhiteSpace(login.TrustToken)) { _cfg.TrustToken = login.TrustToken; _cfg.TrustUsername = user; }
            _cfg.Save();

            await LoadDevicesAsync();
            LoginPanel.IsVisible = false;
            DevicesPanel.IsVisible = true;
        }
        catch (AuthException ax)
        {
            // A saved trust token can expire/revoke server-side; if so, bring the TOTP field back so the
            // operator can type a fresh code and retry.
            if (ax.Code is "totp_required" or "totp_invalid") TotpBox.IsVisible = true;
            LoginStatus.Text = ax.Code switch
            {
                "totp_required" => L.MainForm_EnterTheTOTPCode,
                "totp_invalid" => L.MainForm_InvalidTOTPCode,
                "invalid_credentials" => L.MainForm_SignInFailed,
                "device_locked" => L.MainForm_ThisDeviceIsSignIn,
                _ => L.MainForm_SignInFailed + " (" + ax.Code + ")",
            };
        }
        catch (InvalidOperationException ie) when (ie.Message == "client_outdated")
        {
            LoginStatus.Text = "This console is outdated — the server requires a newer version.";
        }
        catch (InvalidOperationException ie) when (ie.Message == "no_operator_cert")
        {
            LoginStatus.Text = "This account is not enabled for the Linux console (keyless-operator flag is off).";
        }
        catch (Exception ex)
        {
            LoginStatus.Text = "Error: " + ex.Message;
        }
        finally { LoginBtn.IsEnabled = true; }
    }

    private async Task LoadDevicesAsync()
    {
        DevStatus.Text = "Loading…";
        _devices = await _api!.GetDevicesAsync();
        // A sortable/filterable view backs the device table; the grid drives sorting, MatchesSearch filters.
        _view = new DataGridCollectionView(_devices.Select(d => new DeviceRow(d)).ToList()) { Filter = MatchesSearch };
        DeviceList.ItemsSource = _view;
        DevStatus.Text = $"{_devices.Count} device(s)";
    }

    /// <summary>Live search predicate: matches hostname OR group (both columns), case-insensitive.</summary>
    private bool MatchesSearch(object o)
    {
        var q = SearchBox.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        var r = (DeviceRow)o;
        return (r.Hostname?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.Group?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
            || (r.NoteText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void OnSearch(object? sender, TextChangedEventArgs e) => _view?.Refresh();

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        try { await LoadDevicesAsync(); }
        catch (Exception ex) { DevStatus.Text = ex.Message; }
    }

    private async void OnConnect(object? sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceRow sel) { DevStatus.Text = "Select a device first."; return; }
        ConnectBtn.IsEnabled = false;
        try
        {
            // Refresh for the freshest online state + VNC password.
            _devices = await _api!.GetDevicesAsync();
            var d = _devices.FirstOrDefault(x => x.DeviceId == sel.Device.DeviceId) ?? sel.Device;
            if (!d.Online) { DevStatus.Text = "Device is offline."; return; }
            if (string.IsNullOrEmpty(d.VncSecret)) { DevStatus.Text = "No VNC password for this device."; return; }

            DevStatus.Text = $"Opening tunnel to {d.Hostname}…";
            var result = await _api.OpenTunnelAsync(d.DeviceId, "vnc");
            if (result is null) { DevStatus.Text = "Tunnel request failed."; return; }

            DevStatus.Text = "Waiting for the remote device…";
            var outcome = await WaitAccessAsync(result.Nonce);
            if (outcome is not ("auto" or "granted"))
            {
                DevStatus.Text = outcome switch
                {
                    "denied" => "The user at the device denied access.",
                    "timeout" => "No response from the device user.",
                    "no-user" => "No one is signed in at the device.",
                    "locked" => "Remote access is locally disabled on the device.",
                    "cancelled" => "Cancelled.",
                    _ => "The connection was not established.",
                };
                return;
            }

            DevStatus.Text = "Reaching the device through the bastion…";
            await Task.Delay(1500); // give the agent a moment to bring up its reverse tunnel
            var localPort = await _transport!.ForwardAsync(result.RemotePort);
            VncLauncher.Launch(localPort, d.VncSecret!, _cfg.VncScale, _cfg.VncColor256);
            DevStatus.Text = $"VNC started for {d.Hostname}.";
        }
        catch (Exception ex) { DevStatus.Text = "Connection error: " + ex.Message; }
        finally { ConnectBtn.IsEnabled = true; }
    }

    /// <summary>Polls the access-request outcome by nonce until the device answers (or times out).</summary>
    private async Task<string> WaitAccessAsync(string nonce)
    {
        for (int i = 0; i < 60; i++)
        {
            var outcome = await _api!.GetAccessResultAsync(nonce);
            if (!string.IsNullOrEmpty(outcome)) return outcome;
            await Task.Delay(1000);
        }
        return "timeout";
    }

    private void OnUserChanged(object? sender, TextChangedEventArgs e) => UpdateTotpVisibility();

    /// <summary>Hide the TOTP field when a saved 90-day trust token covers the entered username (the server
    /// will skip TOTP for it). OnLogin re-shows it if the server still demands a code.</summary>
    private void UpdateTotpVisibility()
    {
        bool trusted = !string.IsNullOrWhiteSpace(_cfg.TrustToken)
            && string.Equals(_cfg.TrustUsername, UserBox.Text?.Trim(), StringComparison.OrdinalIgnoreCase);
        TotpBox.IsVisible = !trusted;
    }

    // ---- GitHub update notice (no agent/self-update on Linux: just point to the latest release) ----

    private const string RepoOwner = "v1k70rk4";
    private const string RepoName = "RemoteAppClient";

    /// <summary>Fire-and-forget on startup: if the latest GitHub release is newer than the running build,
    /// show a clickable notice on the sign-in page. Fail-silent (offline / rate-limited / no releases yet).</summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            var running = Assembly.GetEntryAssembly()?.GetName().Version;
            if (running is null) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteAppClient-Linux");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
            var tag = tagEl.GetString();
            var latest = ParseVersion(tag);
            if (latest is null) return;
            var run = new Version(running.Major, running.Minor, Math.Max(running.Build, 0));
            if (latest <= run) return; // up to date, or a dev build ahead of the latest release
            UpdateLink.Content = L.Format(L.LinuxConsole_UpdateAvailable, tag!) + "  →";
            UpdateLink.IsVisible = true;
        }
        catch { /* offline / rate-limited / unexpected body - just skip the notice */ }
    }

    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        return Version.TryParse(tag.TrimStart('v', 'V').Trim(), out var v)
            ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    private void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("xdg-open", $"https://github.com/{RepoOwner}/{RepoName}/releases/latest") { UseShellExecute = false }); }
        catch { /* no xdg-open available - ignore */ }
    }

    // ---- Settings (VNC color + scaling) ------------------------------------------------

    private void OnSettings(object? sender, RoutedEventArgs e)
    {
        foreach (var item in ScaleBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == _cfg.VncScale) { ScaleBox.SelectedItem = item; break; }
        if (ScaleBox.SelectedItem is null) ScaleBox.SelectedIndex = 0;
        ColorCheck.IsChecked = _cfg.VncColor256;
        var lang = string.IsNullOrWhiteSpace(_cfg.Language) ? "auto" : _cfg.Language;
        foreach (var item in LangBox.Items.OfType<ComboBoxItem>())
            if (item.Tag as string == lang) { LangBox.SelectedItem = item; break; }
        if (LangBox.SelectedItem is null) LangBox.SelectedIndex = 0;
        SetStatus.Text = "";
        DevicesPanel.IsVisible = false;
        SettingsPanel.IsVisible = true;
    }

    private void OnSettingsSave(object? sender, RoutedEventArgs e)
    {
        if (ScaleBox.SelectedItem is ComboBoxItem ci && ci.Tag is string tag) _cfg.VncScale = tag;
        _cfg.VncColor256 = ColorCheck.IsChecked == true;
        var oldLang = string.IsNullOrWhiteSpace(_cfg.Language) ? "auto" : _cfg.Language;
        if (LangBox.SelectedItem is ComboBoxItem li && li.Tag is string lang) _cfg.Language = lang;
        _cfg.Save();
        if (!string.Equals(oldLang, _cfg.Language, StringComparison.OrdinalIgnoreCase))
        {
            // Language is bound at startup; keep the panel open and tell the user to restart for it to apply.
            SetStatus.Text = L.SettingsView_LanguageSavedRestartAffectedComponents;
            return;
        }
        SettingsPanel.IsVisible = false;
        DevicesPanel.IsVisible = true;
    }

    private void OnSettingsBack(object? sender, RoutedEventArgs e)
    {
        SettingsPanel.IsVisible = false;
        DevicesPanel.IsVisible = true;
    }

    // ---- Password recovery (pre-login, public endpoints; mirrors the Windows ForgotPasswordForm) ----

    private void OnForgot(object? sender, RoutedEventArgs e)
    {
        RecServerBox.Text = string.IsNullOrWhiteSpace(ServerBox.Text) ? (_cfg.LastServerUrl ?? "") : ServerBox.Text;
        RecUserBox.Text = UserBox.Text;
        RecStatus.Text = "";
        LoginPanel.IsVisible = false;
        RecoveryPanel.IsVisible = true;
    }

    private void OnRecoveryBack(object? sender, RoutedEventArgs e)
    {
        RecoveryPanel.IsVisible = false;
        LoginPanel.IsVisible = true;
    }

    private async void OnRecoveryRequest(object? sender, RoutedEventArgs e)
    {
        var server = RecServerBox.Text?.Trim() ?? "";
        var user = RecUserBox.Text?.Trim() ?? "";
        var email = RecEmailBox.Text?.Trim() ?? "";
        if (server.Length == 0) { RecStatus.Foreground = Brushes.IndianRed; RecStatus.Text = "Enter the server URL."; return; }
        if (user.Length == 0 || email.Length == 0) { RecStatus.Foreground = Brushes.IndianRed; RecStatus.Text = L.ForgotPasswordForm_EnterTheUsernameAndEmail; return; }

        try { await LinuxOperatorTransport.RequestPasswordCodeAsync(server, user, email); }
        catch { /* anti-enumeration: never reveal whether the account exists */ }

        // Always neutral feedback, then a 10s cooldown on the request button (same as Windows).
        RecStatus.Foreground = Brushes.Gray;
        RecStatus.Text = L.ForgotPasswordForm_IfTheDetailsAreCorrect;
        RecRequestBtn.IsEnabled = false;
        for (int s = 10; s > 0; s--) { RecRequestBtn.Content = L.Format(L.ForgotPasswordForm_RequestToken_2, s); await Task.Delay(1000); }
        RecRequestBtn.Content = L.ForgotPasswordForm_RequestToken;
        RecRequestBtn.IsEnabled = true;
    }

    private async void OnRecoverySet(object? sender, RoutedEventArgs e)
    {
        var server = RecServerBox.Text?.Trim() ?? "";
        var user = RecUserBox.Text?.Trim() ?? "";
        var code = RecTokenBox.Text?.Trim() ?? "";
        var pw = RecPassBox.Text ?? "";
        RecStatus.Foreground = Brushes.IndianRed;
        if (server.Length == 0 || user.Length == 0 || code.Length == 0) { RecStatus.Text = L.ForgotPasswordForm_EnterTheUsernameAndThe; return; }
        if (pw.Length < 10) { RecStatus.Text = L.ForgotPasswordForm_TheNewPasswordMustBe; return; }

        RecSetBtn.IsEnabled = false;
        try
        {
            var (ok, err) = await LinuxOperatorTransport.ResetPasswordAsync(server, user, code, pw);
            if (ok)
            {
                RecStatus.Foreground = Brushes.SeaGreen;
                RecStatus.Text = L.ForgotPasswordForm_PasswordSetYouCanNow;
                ServerBox.Text = server; UserBox.Text = user; PassBox.Text = "";
                return;
            }
            RecStatus.Text = err switch
            {
                "invalid_code" => L.ForgotPasswordForm_InvalidOrExpiredToken,
                "weak_password" => L.ForgotPasswordForm_TheNewPasswordMustBe,
                "device_locked" => L.ForgotPasswordForm_ThisDeviceIsLockedDue,
                _ => L.ForgotPasswordForm_PasswordSetupFailed,
            };
        }
        catch (Exception ex) { RecStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { RecSetBtn.IsEnabled = true; }
    }

    /// <summary>Shows the selected device's details/telemetry — mirrors the Windows DeviceTelemetryPanel
    /// (same labels, order and formatting). The ConnectPath row is omitted (no local agent on Linux).</summary>
    private void OnDeviceSelected(object? sender, SelectionChangedEventArgs e)
    {
        DetailPanel.Children.Clear();
        if (DeviceList.SelectedItem is not DeviceRow row) return;
        var d = row.Device;

        void Row(string caption, string? value)
        {
            DetailPanel.Children.Add(new TextBlock { Text = caption, FontSize = 11, Opacity = 0.55, Margin = new Thickness(0, 8, 0, 0) });
            DetailPanel.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, TextWrapping = TextWrapping.Wrap });
        }

        Row(L.DevicesView_Device, d.Hostname);
        Row("Online", d.Online ? "online" : "offline");
        Row(L.DevicesView_LastOnline, d.LastSeenAt?.LocalDateTime.ToString("g"));
        Row(L.BootstrapView_Status, d.Status);
        Row(L.BootstrapView_Group, string.IsNullOrWhiteSpace(d.GroupName) ? L.BootstrapView_NoGroup : d.GroupName);
        Row(L.DeviceTelemetryPanel_Channel, string.Equals(d.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");
        Row(L.DeviceTelemetryPanel_SignedInUser, d.LoggedInUser ?? L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_IPAddressLocal, d.IpAddress);
        Row(L.DeviceTelemetryPanel_PublicIP, PublicIp(d));
        Row("Wi-Fi", string.IsNullOrWhiteSpace(d.WifiSsid) ? L.DeviceTelemetryPanel_WiredNoWiFi : d.WifiSsid);
        Row("VPN", d.VpnActive ? L.DeviceTelemetryPanel_Active : L.DeviceTelemetryPanel_No);
        Row(L.DeviceTelemetryPanel_BootTime, d.BootTimeUtc?.LocalDateTime.ToString("g"));
        Row(L.DeviceTelemetryPanel_Uptime, Uptime(d.BootTimeUtc));
        Row(L.DeviceTelemetryPanel_MakeModel, $"{(string.IsNullOrWhiteSpace(d.Manufacturer) ? "OEM" : d.Manufacturer)} / {S(d.Model)}");
        Row(L.DeviceTelemetryPanel_Serial, d.SerialNumber);
        Row(L.DeviceTelemetryPanel_LocalLock, d.VncLocked ? L.DeviceTelemetryPanel_DISABLED : "—");
        Row(L.DeviceTelemetryPanel_SignInLock, d.LoginLocked
            ? L.Format(L.DeviceTelemetryPanel_LOCKEDFailed, d.LoginFailCount)
            : (d.LoginFailCount > 0 ? L.Format(L.DeviceTelemetryPanel_FailedAttempt, d.LoginFailCount) : "—"));
        Row("Agent / Helper / VNC", $"{S(d.AgentVersion)} / {S(d.HelperVersion)} / {S(d.VncVersion)}");
        Row(L.DeviceTelemetryPanel_ClientOS, $"{S(d.ClientVersion)} / {S(d.OsVersion)}");
        Row(L.DeviceTelemetryPanel_AgentRestarts, d.AgentRestarts.ToString());
        if (!string.IsNullOrWhiteSpace(d.LastIncident)) Row(L.DeviceTelemetryPanel_LastIncident, d.LastIncident);
        if (!string.IsNullOrWhiteSpace(d.Note)) Row(L.DeviceGeneralPanel_Note, d.Note);
        Row("deviceId", d.DeviceId);
    }

    private static string S(string? v) => string.IsNullOrWhiteSpace(v) ? "—" : v;

    /// <summary>"reverse (ip)" when a PTR is cached, else just the IP, else "—" (same as the Windows panel).</summary>
    private static string PublicIp(DeviceInfo d) =>
        string.IsNullOrWhiteSpace(d.PublicIpAddress) ? "—"
        : string.IsNullOrWhiteSpace(d.PublicIpReverse) ? d.PublicIpAddress!
        : $"{d.PublicIpReverse} ({d.PublicIpAddress})";

    private static string? Uptime(DateTimeOffset? boot)
    {
        if (boot is not { } b || b == default) return null;
        var t = DateTimeOffset.UtcNow - b;
        if (t < TimeSpan.Zero) return null;
        if (t.TotalDays >= 1) return L.Format(L.DeviceTelemetryPanel_DayHour, (int)t.TotalDays, t.Hours);
        if (t.TotalHours >= 1) return L.Format(L.DeviceTelemetryPanel_HourMinute, (int)t.TotalHours, t.Minutes);
        return $"{t.Minutes} perc";
    }

    /// <summary>List row: a friendly hostname line plus an online/last-seen subline. Holds the device for connect.</summary>
    public sealed class DeviceRow(DeviceInfo device)
    {
        public DeviceInfo Device { get; } = device;
        public string Hostname => string.IsNullOrEmpty(Device.Hostname) ? Device.DeviceId : Device.Hostname;
        public string Group => Device.GroupName ?? "";
        public string NoteText => Device.Note ?? "";
        public string OnlineText => Device.Online ? "online" : "offline";
        public DateTimeOffset? LastSeen => Device.LastSeenAt;
    }
}
