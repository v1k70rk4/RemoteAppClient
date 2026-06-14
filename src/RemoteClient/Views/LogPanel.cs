using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Embedded audit log list, optionally fixed to one deviceId or actor. Event type filter is always
/// available. Keys are localized by <see cref="AuditText"/>.
/// </summary>
public sealed class LogPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string? _deviceId;
    private readonly string? _actor;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _filter = new() { Hint = L.LogPanel_Event, Width = 240 };
    private readonly MaterialLabel _status = new();

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }

    public LogPanel(AdminApi api, string? deviceId = null, string? actor = null)
    {
        _api = api; _deviceId = deviceId; _actor = actor;
        Dock = DockStyle.Fill;

        _filter.Items.AddRange(new object[]
        {
            new FilterItem(null, L.LogPanel_All),
            new FilterItem("connect", L.AuditText_ConnectionWithConsent),
            new FilterItem("connect-auto", L.AuditText_ConnectionWithoutConsent),
            new FilterItem("access-denied", L.DevicesView_Denied),
            new FilterItem("access-locked", L.LogPanel_DisabledDevice),
            new FilterItem("device.enrolled", L.LogPanel_Enrollment),
            new FilterItem("device-update", L.AuditText_DeviceUpdated),
            new FilterItem("rollout", "Rollout"),
        });
        _filter.SelectedIndex = 0;
        _filter.Margin = new Padding(4, 0, 12, 0);

        var tools = ViewUi.Toolbar();
        var refresh = ViewUi.ToolbarButton(L.AboutView_Refresh);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.AddRange([_filter, refresh]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.LogPanel_Time, 150);
        _list.Columns.Add(L.LogPanel_Who, 120);
        _list.Columns.Add(L.LogPanel_Event, 220);
        _list.Columns.Add(L.DevicesView_Device, 140);
        _list.Columns.Add(L.LogPanel_Detail, 200);

        Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
    }

    public async Task ShownAsync()
    {
        ThemeManager.StyleList(_list);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var key = (_filter.SelectedItem as FilterItem)?.Key;
            var rows = await _api.GetAuditAsync(action: key, actor: _actor, deviceId: _deviceId, limit: 500);
            _list.Items.Clear();
            foreach (var e in rows)
            {
                var item = new ListViewItem(e.CreatedAt.LocalDateTime.ToString("g")) { Tag = e };
                item.SubItems.Add(e.Actor);
                item.SubItems.Add(AuditText.Hu(e.Action));
                item.SubItems.Add(e.Target ?? "—");
                item.SubItems.Add(AuditText.Detail(e.Detail));
                if (AuditText.IsNegative(e.Action)) item.ForeColor = Color.IndianRed;
                else if (AuditText.IsNoConsent(e.Action)) item.ForeColor = Color.DarkOrange;
                _list.Items.Add(item);
            }
            _status.Text = L.Format(L.LogPanel_Entry, rows.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
