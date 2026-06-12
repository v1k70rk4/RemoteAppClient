using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Release-csatornák nézet: aktuális csomagok, rollout, BETA→RTM promote, exe-feltöltés, MSI — egy ablakon belül.</summary>
public sealed class ChannelsView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _component = new() { Hint = "Komponens" };
    private readonly MaterialLabel _status = new();

    public ChannelsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _list.View = View.Details; _list.FullRowSelect = true;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Csatorna", 90);
        _list.Columns.Add("Komponens", 100);
        _list.Columns.Add("Verzió", 130);
        _list.Columns.Add("Feltöltve", 160);

        _component.Width = 150; _component.Margin = new Padding(4, 0, 12, 0);
        _component.Items.AddRange(["agent", "updater"]);
        _component.SelectedIndex = 0;

        MaterialButton Mk(string text, bool primary, EventHandler onClick)
        {
            var b = ViewUi.ToolbarButton(text, primary);
            b.Click += onClick;
            return b;
        }
        var tools = ViewUi.Toolbar();
        tools.Controls.Add(_component);
        tools.Controls.AddRange([
            Mk("Rollout RTM", true, async (_, _) => await RolloutAsync("rtm")),
            Mk("Rollout BETA", true, async (_, _) => await RolloutAsync("beta")),
            Mk("Promote BETA→RTM", false, async (_, _) => await PromoteAsync()),
            Mk("Exe feltöltés…", false, async (_, _) =>
            {
                using var f = new UploadPackageForm(_api);
                if (f.ShowDialog(this) == DialogResult.OK) await RefreshAsync();
            }),
            Mk("MSI gyártás…", false, async (_, _) =>
            {
                try { var groups = await _api.GetGroupsAsync(); using var f = new MsiForm(_api, groups); f.ShowDialog(this); }
                catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
            }),
        ]);

        _status.Text = "…";
        Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
        ApplyTheme();
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() => await RefreshAsync();

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
