using System.Diagnostics;
using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>About view: program icon/name/client version, component versions/state, server, channel, and connection.</summary>
public sealed class AboutView : UserControl, IContentView
{
    private readonly ClientConfig _cfg;
    private readonly TableLayoutPanel _tbl = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(24, 4, 24, 12) };
    private readonly MaterialLabel _status = new();
    private int _row;

    public AboutView(ClientConfig cfg)
    {
        _cfg = cfg;
        Dock = DockStyle.Fill;

        _tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // Header: icon, name, and client version.
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(24, 18, 24, 6) };
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ico is not null) header.Controls.Add(new PictureBox { Image = ico.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 12, 0) });
        }
        catch { /* icon is optional */ }
        var titleCol = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        titleCol.Controls.Add(new MaterialLabel { Text = "RemoteAppClient", Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true });
        titleCol.Controls.Add(new MaterialLabel { Text = L.AboutView_ClientVersion + ClientUpdater.RunningVersionString(), AutoSize = true });
        header.Controls.Add(titleCol);

        var refresh = ViewUi.ToolbarButton(L.AboutView_Refresh, primary: false);
        refresh.Click += async (_, _) => await LoadAsync();
        var refreshRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(24, 0, 24, 4) };
        refreshRow.Controls.Add(refresh);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(_tbl);

        Controls.Add(scroll);
        Controls.Add(refreshRow);
        Controls.Add(header);
        Controls.Add(ViewUi.StatusHost(_status));
    }

    public void ApplyTheme() => ThemeManager.StyleView(this);

    public async Task OnShownAsync() => await LoadAsync();

    private void Section(string title)
    {
        var l = new MaterialLabel { Text = title, Font = new Font("Segoe UI", 12F, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 14, 0, 4) };
        _tbl.Controls.Add(l, 0, _row);
        _tbl.SetColumnSpan(l, 2);
        _row++;
    }

    private void Row(string caption, string value, Color? color = null)
    {
        var cap = new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 3, 24, 0) };
        var val = new MaterialLabel { Text = value, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        if (color is { } c) val.ForeColor = c;
        _tbl.Controls.Add(cap, 0, _row);
        _tbl.Controls.Add(val, 1, _row);
        _row++;
    }

    /// <summary>Clickable email row using mailto:.</summary>
    private void RowMail(string caption, string email)
    {
        var cap = new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 3, 24, 0) };
        var val = new MaterialLabel { Text = email, AutoSize = true, Margin = new Padding(0, 3, 0, 0), ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand };
        val.Font = new Font(val.Font, FontStyle.Underline);
        val.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("mailto:" + email) { UseShellExecute = true }); } catch { /* no mail client */ } };
        _tbl.Controls.Add(cap, 0, _row);
        _tbl.Controls.Add(val, 1, _row);
        _row++;
    }

    private async Task LoadAsync()
    {
        _status.Text = L.AboutView_FetchingStatus;
        var s = await StatusClient.QueryAgentAsync();
        _tbl.SuspendLayout();
        _tbl.Controls.Clear();
        _row = 0;

        Section(L.AboutView_Connection);
        if (s is null)
        {
            Row(L.AboutView_LocalAgent, L.AboutView_Unavailable, Color.IndianRed);
        }
        else
        {
            Row(L.AboutView_ServerC2, s.C2Connected ? "● Online" : "● Offline", s.C2Connected ? Color.MediumSeaGreen : Color.IndianRed);
            Row("Tunnel", s.TunnelActive ? L.AboutView_Ready : L.AboutView_Stopped);
            Row(L.AboutView_Transport, s.ActiveBastionPort switch
            {
                443 => "443 (sslh)",
                22 => "22 (ssh)",
                -1 => "443 (WSS)",
                > 0 => s.ActiveBastionPort.ToString(),
                _ => (s.BastionTransport ?? "auto") switch
                {
                    "ssl443" => "443 (sslh)",
                    "ssh22" => "22 (ssh)",
                    "wss443" => "443 (WSS)",
                    _ => "Auto (443 → WSS)",
                },
            });
            Row(L.AboutView_LastServerContact, s.LastServerContactUtc?.LocalDateTime.ToString("g") ?? "—");
        }

        Section(L.AboutView_Server);
        Row(L.AboutView_Address, AgentInfo.ServerName());
        Row(L.AboutView_UpdateChannel, string.Equals(_cfg.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");

        var brand = BrandingCache.Load();
        if (brand is not null && (!string.IsNullOrWhiteSpace(brand.OwnerName) || !string.IsNullOrWhiteSpace(brand.SupportPhone) || !string.IsNullOrWhiteSpace(brand.SupportEmail)))
        {
            Section(L.AboutView_Support);
            if (!string.IsNullOrWhiteSpace(brand.OwnerName)) Row(L.AboutView_Owner, brand.OwnerName!);
            if (!string.IsNullOrWhiteSpace(brand.SupportPhone)) Row(L.AboutView_Phone, brand.SupportPhone!);
            if (!string.IsNullOrWhiteSpace(brand.SupportEmail)) RowMail("E-mail", brand.SupportEmail!);
        }

        Section(L.AboutView_ComponentsOnThisDevice);
        Row("Agent", s is null ? "—" : $"{s.Version} · {(s.C2Connected ? L.AboutView_RunningOnline : L.AboutView_Running)}", s is null ? Color.Gray : Color.MediumSeaGreen);
        Row("Helper (updater)", s?.HelperVersion ?? "—");
        Row(L.AboutView_ClientConsole, ClientUpdater.RunningVersionString() + " · " + L.AboutView_Running);
        Row("TightVNC", s?.VncVersion ?? "—");

        _tbl.ResumeLayout();
        _status.Text = s is null ? L.AboutView_TheLocalAgentIsNot : L.AboutView_Upd;
    }
}
