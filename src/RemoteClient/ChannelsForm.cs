using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>
/// Release-csatornák kezelése: az aktuális csomagok (rtm/beta × agent/updater),
/// rollout egy gyűrűre, BETA→RTM előléptetés, exe-feltöltés és MSI-gyártás.
/// </summary>
public sealed class ChannelsForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _component = new() { Hint = "Komponens" };
    private readonly MaterialLabel _status = new();

    public ChannelsForm(AdminApi api)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Release csatornák";
        Sizable = false;
        Width = 600; Height = 540;
        StartPosition = FormStartPosition.CenterParent;

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Csatorna", 90);
        _list.Columns.Add("Komponens", 100);
        _list.Columns.Add("Verzió", 130);
        _list.Columns.Add("Feltöltve", 160);
        ThemeManager.StyleList(_list);

        _component.Width = 150;
        _component.Items.AddRange(["agent", "updater"]);
        _component.SelectedIndex = 0;
        var compRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(8, 6, 8, 0), WrapContents = false };
        compRow.Controls.Add(_component);

        MaterialButton Mk(string text, bool outlined, EventHandler onClick)
        {
            var b = new MaterialButton { Text = text, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
            if (outlined) { b.Type = MaterialButton.MaterialButtonType.Outlined; b.HighEmphasis = false; }
            b.Click += onClick;
            return b;
        }
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8, 4, 8, 0), WrapContents = false };
        actionRow.Controls.AddRange([
            Mk("Rollout RTM", false, async (_, _) => await RolloutAsync("rtm")),
            Mk("Rollout BETA", false, async (_, _) => await RolloutAsync("beta")),
            Mk("Promote BETA→RTM", true, async (_, _) => await PromoteAsync()),
        ]);

        var pkgRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(8, 4, 8, 0), WrapContents = false };
        pkgRow.Controls.AddRange([
            Mk("Exe feltöltés…", true, async (_, _) =>
            {
                using var f = new UploadPackageForm(_api);
                if (f.ShowDialog(this) == DialogResult.OK) await RefreshAsync();
            }),
            Mk("MSI gyártás…", true, async (_, _) =>
            {
                try { var groups = await _api.GetGroupsAsync(); using var f = new MsiForm(_api, groups); f.ShowDialog(this); }
                catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
            }),
        ]);

        var bottom = new MaterialCard { Dock = DockStyle.Bottom, Height = 40, Margin = new Padding(0) };
        _status.Dock = DockStyle.Fill; _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 0, 0);
        _status.Text = "…";
        bottom.Controls.Add(_status);

        // Dokk-sorrend: a Fill-t adjuk először, majd a Bottom-sorokat fentről lefelé (az elsőként
        // hozzáadott Bottom kerül a lista alá, az utolsó a legalsó szélre).
        Controls.Add(_list);
        Controls.Add(compRow);
        Controls.Add(actionRow);
        Controls.Add(pkgRow);
        Controls.Add(bottom);

        Load += async (_, _) => await RefreshAsync();
    }

    private string Comp => _component.SelectedItem?.ToString() ?? "agent";

    private async Task RefreshAsync()
    {
        try
        {
            var ch = await _api.GetChannelsAsync();
            _list.Items.Clear();
            foreach (var p in ch)
            {
                var it = new ListViewItem(p.Channel.ToUpperInvariant());
                it.SubItems.Add(p.Component);
                it.SubItems.Add(p.Version);
                it.SubItems.Add(p.UploadedAt.LocalDateTime.ToString("g"));
                _list.Items.Add(it);
            }
            _status.Text = ch.Count == 0 ? "Még nincs feltöltött csomag egy csatornán sem." : $"{ch.Count} aktuális csomag.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task RolloutAsync(string channel)
    {
        if (MessageBox.Show($"Kiadod a(z) {channel.ToUpperInvariant()} csatorna aktuális '{Comp}' csomagját az ott lévő, frissíthető gépeknek?",
                "Rollout", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { _status.Text = "Rollout: " + await _api.RolloutAsync(channel, Comp); }
        catch (Exception ex) { _status.Text = "Rollout hiba: " + ex.Message; }
    }

    private async Task PromoteAsync()
    {
        if (MessageBox.Show($"Előlépteted a BETA aktuális '{Comp}' csomagját RTM-be (ugyanaz a fájl)?",
                "Promote", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var res = await _api.PromoteAsync("beta", Comp, "rtm");
            await RefreshAsync();
            _status.Text = "Promote: " + res;
        }
        catch (Exception ex) { _status.Text = "Promote hiba: " + ex.Message; }
    }
}
