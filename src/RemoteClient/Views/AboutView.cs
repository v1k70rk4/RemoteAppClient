using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>About view: program icon/name/client version + key/value cards (Connection, Server & support,
/// Components on this device). Refresh re-queries the local agent. See design_handoff_console_redesign.</summary>
public sealed class AboutView : UserControl, IContentView
{
    private const int CardW = 600, ContentW = CardW - 36;

    private readonly ClientConfig _cfg;
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(22, 4, 22, 22) };
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    public AboutView(ClientConfig cfg)
    {
        _cfg = cfg;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        var header = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = ThemeManager.Bg, Padding = new Padding(22, 0, 22, 0) };
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ico is not null) header.Controls.Add(new PictureBox { Image = ico.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(22, 16) });
        }
        catch { /* icon is optional */ }
        header.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, "RemoteAppClient", UiFont.PageTitle, new Rectangle(78, 16, CardW - 200, 24),
                ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, L.AboutView_ClientVersion + ClientUpdater.RunningVersionString(), UiFont.Small,
                new Rectangle(78, 42, CardW - 200, 18), ThemeManager.Text2, TextFormatFlags.Left | TextFormatFlags.NoPadding);
        };
        var refresh = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline);
        refresh.Location = new Point(22 + CardW - refresh.Width, 18);
        refresh.Click += async (_, _) => await LoadAsync();
        header.Controls.Add(refresh);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ThemeManager.Bg, Padding = new Padding(24, 0, 0, 0) };
        statusHost.Controls.Add(_status);

        _scroll.Controls.Add(_cards);
        Controls.Add(_scroll);
        Controls.Add(statusHost);
        Controls.Add(header);
    }

    public void ApplyTheme() { BackColor = ThemeManager.Bg; Invalidate(true); }

    public async Task OnShownAsync() => await LoadAsync();

    private Card MakeCard(string title, List<(string Cap, string Val, Color? Color, Font? Font)> rows)
    {
        var body = new Panel();
        for (int i = 0; i < rows.Count; i++)
        {
            var (cap, val, color, font) = rows[i];
            body.Controls.Add(new KvRow(cap, string.IsNullOrWhiteSpace(val) ? "—" : val, color ?? ThemeManager.Text, font ?? UiFont.Mono)
            { Location = new Point(0, i * 38), Width = ContentW });
        }
        return new Card(title, null, body) { Width = CardW, Height = 46 + rows.Count * 38 + 10, Margin = new Padding(0, 0, 0, 14) };
    }

    private async Task LoadAsync()
    {
        _status.Text = L.AboutView_FetchingStatus;
        var s = await StatusClient.QueryAgentAsync();

        var conn = new List<(string, string, Color?, Font?)>();
        if (s is null)
            conn.Add((L.AboutView_LocalAgent, L.AboutView_Unavailable, ThemeManager.DangerFg, UiFont.Body));
        else
        {
            conn.Add((L.AboutView_ServerC2, s.C2Connected ? "● Online" : "● Offline", s.C2Connected ? ThemeManager.OkFg : ThemeManager.DangerFg, UiFont.Body));
            conn.Add(("Tunnel", s.TunnelActive ? L.AboutView_Ready : L.AboutView_Stopped, null, UiFont.Body));
            string transport = s.ActiveBastionPort switch
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
            };
            conn.Add((L.AboutView_Transport, transport, null, UiFont.Mono));
            conn.Add((L.AboutView_LastServerContact, s.LastServerContactUtc?.LocalDateTime.ToString("g") ?? "—", null, UiFont.Mono));
        }

        var srv = new List<(string, string, Color?, Font?)>
        {
            (L.AboutView_Address, AgentInfo.ServerName(), null, UiFont.Mono),
            (L.AboutView_UpdateChannel, string.Equals(_cfg.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm", null, UiFont.Body),
        };
        var brand = BrandingCache.Load();
        if (brand is not null)
        {
            if (!string.IsNullOrWhiteSpace(brand.OwnerName)) srv.Add((L.AboutView_Owner, brand.OwnerName!, null, UiFont.Body));
            if (!string.IsNullOrWhiteSpace(brand.SupportPhone)) srv.Add((L.AboutView_Phone, brand.SupportPhone!, null, UiFont.Mono));
            if (!string.IsNullOrWhiteSpace(brand.SupportEmail)) srv.Add(("E-mail", brand.SupportEmail!, ThemeManager.Accent, UiFont.Body));
        }

        var comp = new List<(string, string, Color?, Font?)>
        {
            ("Agent", s is null ? "—" : $"{s.Version} · {(s.C2Connected ? L.AboutView_RunningOnline : L.AboutView_Running)}", s is null ? ThemeManager.Text3 : ThemeManager.OkFg, UiFont.Body),
            ("Helper (updater)", s?.HelperVersion ?? "—", null, UiFont.Mono),
            (L.AboutView_ClientConsole, ClientUpdater.RunningVersionString() + " · " + L.AboutView_Running, null, UiFont.Body),
            ("TightVNC", s?.VncVersion ?? "—", null, UiFont.Mono),
        };

        _cards.SuspendLayout();
        _cards.Controls.Clear();
        _cards.Controls.Add(MakeCard(L.AboutView_Connection, conn));
        _cards.Controls.Add(MakeCard(L.AboutView_Server, srv));
        _cards.Controls.Add(MakeCard(L.AboutView_ComponentsOnThisDevice, comp));
        _cards.ResumeLayout();
        _status.Text = s is null ? L.AboutView_TheLocalAgentIsNot : L.AboutView_Upd;
    }
}
