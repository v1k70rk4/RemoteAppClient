using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Napló (audit): távoli elérés + admin műveletek, szűrhetően. A kulcsokat a kliens fordítja.</summary>
public sealed class LogView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _filter = new() { Hint = L.LogPanel_001, Width = 240 };
    private readonly MaterialTextBox2 _actor = new() { Hint = L.LogView_001, Width = 200 };
    private readonly MaterialLabel _status = new();

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }

    public LogView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _filter.Items.AddRange(new object[]
        {
            new FilterItem(null, L.LogPanel_007),
            new FilterItem("connect", L.AuditText_001),
            new FilterItem("connect-auto", L.AuditText_002),
            new FilterItem("access-denied", L.DevicesView_021),
            new FilterItem("access-locked", L.LogPanel_002),
            new FilterItem("device.enrolled", L.LogPanel_003),
            new FilterItem("device-update", L.AuditText_008),
            new FilterItem("user-create", L.AuditText_012),
            new FilterItem("user-update", L.AuditText_013),
            new FilterItem("rollout", "Rollout"),
            new FilterItem("msi-build", L.AuditText_023),
        });
        _filter.SelectedIndex = 0;
        _filter.Margin = new Padding(4, 0, 12, 0);
        _actor.Margin = new Padding(4, 0, 12, 0);

        var tools = ViewUi.Toolbar();
        var refresh = ViewUi.ToolbarButton(L.AboutView_002);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.AddRange([_filter, _actor, refresh]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.LogPanel_004, 150);
        _list.Columns.Add(L.LogPanel_008, 120);
        _list.Columns.Add(L.LogPanel_001, 220);
        _list.Columns.Add(L.DevicesView_003, 150);
        _list.Columns.Add(L.LogPanel_005, 220);

        Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
        ApplyTheme();
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var key = (_filter.SelectedItem as FilterItem)?.Key;
            var actor = string.IsNullOrWhiteSpace(_actor.Text) ? null : _actor.Text.Trim();
            var rows = await _api.GetAuditAsync(action: key, actor: actor, limit: 500);
            _list.Items.Clear();
            foreach (var e in rows)
            {
                var item = new ListViewItem(e.CreatedAt.LocalDateTime.ToString("g")) { Tag = e };
                item.SubItems.Add(e.Actor);
                item.SubItems.Add(AuditText.Hu(e.Action));
                item.SubItems.Add(e.Target ?? "—");
                item.SubItems.Add(e.Detail ?? "—");
                if (AuditText.IsNegative(e.Action)) item.ForeColor = Color.IndianRed;
                else if (AuditText.IsNoConsent(e.Action)) item.ForeColor = Color.DarkOrange;
                _list.Items.Add(item);
            }
            _status.Text = L.Format(L.LogPanel_006, rows.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; }
    }
}
