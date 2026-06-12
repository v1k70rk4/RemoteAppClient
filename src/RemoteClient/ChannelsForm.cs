using RemoteAgent.Admin;

namespace RemoteClient;

/// <summary>
/// Release-csatornák kezelése: az aktuális csomagok (rtm/beta × agent/updater),
/// rollout egy gyűrűre, és BETA→RTM előléptetés. A feltöltés (exe) későbbi lépés (MSI-task).
/// </summary>
public sealed class ChannelsForm : Form
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly ComboBox _component = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _status = new();

    public ChannelsForm(AdminApi api)
    {
        _api = api;
        Text = "Release csatornák";
        Width = 560; Height = 380;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false;

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Top; _list.Height = 190;
        _list.Columns.Add("Csatorna", 90);
        _list.Columns.Add("Komponens", 90);
        _list.Columns.Add("Verzió", 120);
        _list.Columns.Add("Feltöltve", 150);

        AddLabel("Komponens:", 12, 202);
        _component.SetBounds(92, 198, 120, 24);
        _component.Items.AddRange(["agent", "updater"]);
        _component.SelectedIndex = 0;

        var rolloutRtm = new Button { Text = "Rollout RTM", Bounds = new Rectangle(12, 236, 120, 32) };
        rolloutRtm.Click += async (_, _) => await RolloutAsync("rtm");
        var rolloutBeta = new Button { Text = "Rollout BETA", Bounds = new Rectangle(140, 236, 120, 32) };
        rolloutBeta.Click += async (_, _) => await RolloutAsync("beta");
        var promote = new Button { Text = "Promote BETA→RTM", Bounds = new Rectangle(268, 236, 170, 32) };
        promote.Click += async (_, _) => await PromoteAsync();

        _status.SetBounds(12, 278, 530, 46);
        _status.Text = "…";

        Controls.AddRange([_list, _component, rolloutRtm, rolloutBeta, promote, _status]);
        Load += async (_, _) => await RefreshAsync();
    }

    private void AddLabel(string t, int x, int y) =>
        Controls.Add(new Label { Text = t, Bounds = new Rectangle(x, y, 78, 22) });

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
