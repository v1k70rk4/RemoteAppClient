using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Napló (audit): távoli elérés + admin műveletek, szűrhetően. A kulcsokat a kliens fordítja.</summary>
public sealed class LogView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly MaterialComboBox _filter = new() { Hint = "Esemény", Width = 240 };
    private readonly MaterialTextBox2 _actor = new() { Hint = "Felhasználó (opcionális)", Width = 200 };
    private readonly MaterialLabel _status = new();

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }

    public LogView(AdminApi api)
    {
        _api = api;
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
            new FilterItem("user-create", "Felhasználó létrehozva"),
            new FilterItem("user-update", "Felhasználó módosítva"),
            new FilterItem("rollout", "Rollout"),
            new FilterItem("msi-build", "MSI gyártva"),
        });
        _filter.SelectedIndex = 0;
        _filter.Margin = new Padding(4, 0, 12, 0);
        _actor.Margin = new Padding(4, 0, 12, 0);

        var tools = ViewUi.Toolbar();
        var refresh = ViewUi.ToolbarButton("Frissítés");
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.AddRange([_filter, _actor, refresh]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Idő", 150);
        _list.Columns.Add("Ki", 120);
        _list.Columns.Add("Esemény", 220);
        _list.Columns.Add("Gép", 150);
        _list.Columns.Add("Részlet", 220);

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
            _status.Text = $"{rows.Count} bejegyzés.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
