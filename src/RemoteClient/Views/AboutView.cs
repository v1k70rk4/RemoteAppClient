using System.Diagnostics;
using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>Névjegy: program ikon + név + kliens verzió; komponensek verziói/állapota, szerver, csatorna, kapcsolat.</summary>
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

        // Fejléc: ikon + név + kliens verzió.
        var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(24, 18, 24, 6) };
        try
        {
            var ico = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if (ico is not null) header.Controls.Add(new PictureBox { Image = ico.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 12, 0) });
        }
        catch { /* ikon nélkül is jó */ }
        var titleCol = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        titleCol.Controls.Add(new MaterialLabel { Text = "RemoteAppClient", Font = new Font("Segoe UI", 15F, FontStyle.Bold), AutoSize = true });
        titleCol.Controls.Add(new MaterialLabel { Text = "Kliens verzió: " + ClientUpdater.RunningVersionString(), AutoSize = true });
        header.Controls.Add(titleCol);

        var refresh = ViewUi.ToolbarButton("Frissítés", primary: false);
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

    /// <summary>Kattintható e-mail sor (mailto:).</summary>
    private void RowMail(string caption, string email)
    {
        var cap = new MaterialLabel { Text = caption, AutoSize = true, FontType = MaterialSkinManager.fontType.Caption, Margin = new Padding(0, 3, 24, 0) };
        var val = new MaterialLabel { Text = email, AutoSize = true, Margin = new Padding(0, 3, 0, 0), ForeColor = Color.DodgerBlue, Cursor = Cursors.Hand };
        val.Font = new Font(val.Font, FontStyle.Underline);
        val.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("mailto:" + email) { UseShellExecute = true }); } catch { /* nincs levelező */ } };
        _tbl.Controls.Add(cap, 0, _row);
        _tbl.Controls.Add(val, 1, _row);
        _row++;
    }

    private async Task LoadAsync()
    {
        _status.Text = "Állapot lekérése…";
        var s = await StatusClient.QueryAgentAsync();
        _tbl.SuspendLayout();
        _tbl.Controls.Clear();
        _row = 0;

        Section("Kapcsolat");
        if (s is null)
        {
            Row("Helyi agent", "nem elérhető", Color.IndianRed);
        }
        else
        {
            Row("Szerver (C2)", s.C2Connected ? "● Online" : "● Offline", s.C2Connected ? Color.MediumSeaGreen : Color.IndianRed);
            Row("Tunnel", s.TunnelActive ? "kész" : "áll");
            Row("Utolsó szerver-kontakt", s.LastServerContactUtc?.LocalDateTime.ToString("g") ?? "—");
        }

        Section("Szerver");
        Row("Cím", AgentInfo.ServerName());
        Row("Frissítési csatorna", string.Equals(_cfg.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? "BETA" : "rtm");

        var brand = BrandingCache.Load();
        if (brand is not null && (!string.IsNullOrWhiteSpace(brand.OwnerName) || !string.IsNullOrWhiteSpace(brand.SupportPhone) || !string.IsNullOrWhiteSpace(brand.SupportEmail)))
        {
            Section("Támogatás");
            if (!string.IsNullOrWhiteSpace(brand.OwnerName)) Row("Tulajdonos", brand.OwnerName!);
            if (!string.IsNullOrWhiteSpace(brand.SupportPhone)) Row("Telefon", brand.SupportPhone!);
            if (!string.IsNullOrWhiteSpace(brand.SupportEmail)) RowMail("E-mail", brand.SupportEmail!);
        }

        Section("Komponensek (ezen a gépen)");
        Row("Agent", s is null ? "—" : $"{s.Version} · {(s.C2Connected ? "fut, online" : "fut")}", s is null ? Color.Gray : Color.MediumSeaGreen);
        Row("Helper (updater)", s?.HelperVersion ?? "—");
        Row("Kliens (konzol)", ClientUpdater.RunningVersionString() + " · fut");
        Row("TightVNC", s?.VncVersion ?? "—");

        _tbl.ResumeLayout();
        _status.Text = s is null ? "A helyi agent nem válaszol." : "Friss.";
    }
}
