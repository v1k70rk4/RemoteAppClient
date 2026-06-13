using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient.Views;

/// <summary>
/// Beágyazható napló-lista (audit). Opcionálisan egy gépre (deviceId) vagy felhasználóra (actor)
/// rögzítve. Az esemény-típus szűrő mindig elérhető. A kulcsokat az <see cref="AuditText"/> fordítja.
/// </summary>
public sealed class LogPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string? _deviceId;
    private readonly string? _actor;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _filter = new() { Hint = "Esemény", Width = 240 };
    private readonly MaterialLabel _status = new();

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }

    public LogPanel(AdminApi api, string? deviceId = null, string? actor = null)
    {
        _api = api; _deviceId = deviceId; _actor = actor;
        Dock = DockStyle.Fill;

        _filter.Items.AddRange(new object[]
        {
            new FilterItem(null, "Mind"),
            new FilterItem("connect", "Csatlakozás (hozzájárulással)"),
            new FilterItem("connect-auto", "Csatlakozás (hozzájárulás nélkül)"),
            new FilterItem("access-denied", "Elutasítva"),
            new FilterItem("access-locked", "Letiltott gép"),
            new FilterItem("device.enrolled", "Beléptetés"),
            new FilterItem("device-update", "Eszköz módosítva"),
            new FilterItem("rollout", "Rollout"),
        });
        _filter.SelectedIndex = 0;
        _filter.Margin = new Padding(4, 0, 12, 0);

        var tools = ViewUi.Toolbar();
        var refresh = ViewUi.ToolbarButton("Frissítés");
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.AddRange([_filter, refresh]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Idő", 150);
        _list.Columns.Add("Ki", 120);
        _list.Columns.Add("Esemény", 220);
        _list.Columns.Add("Gép", 140);
        _list.Columns.Add("Részlet", 200);

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
                item.SubItems.Add(e.Detail ?? "—");
                if (AuditText.IsNegative(e.Action)) item.ForeColor = Color.IndianRed;
                else if (AuditText.IsNoConsent(e.Action)) item.ForeColor = Color.DarkOrange;
                _list.Items.Add(item);
            }
            _status.Text = $"{rows.Count} bejegyzés.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
