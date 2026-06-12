using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Eszközcsoportok kezelése: létrehozás, szerkesztés (név + consent/unattended), törlés.</summary>
public sealed class GroupsView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    public GroupsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        var tools = ViewUi.Toolbar();
        void Btn(string text, Func<Task> onClick) { var b = ViewUi.ToolbarButton(text); b.Click += async (_, _) => await onClick(); tools.Controls.Add(b); }
        Btn("Új csoport…", NewAsync);
        Btn("Szerkesztés", EditAsync);
        Btn("Törlés", DeleteAsync);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Csoport", 220);
        _list.Columns.Add("Consent kell", 110);
        _list.Columns.Add("Unattended", 110);
        _list.Columns.Add("Gépek", 80);
        _list.DoubleClick += async (_, _) => await EditAsync();

        Controls.Add(ViewUi.Rows(1, tools, _list, ViewUi.StatusHost(_status)));
        ApplyTheme();
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() => await RefreshAsync();

    private GroupInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (GroupInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync()
    {
        try
        {
            var groups = await _api.GetGroupsAsync();
            // Gépszám csoportonként (az eszközlistából).
            var devices = await _api.GetDevicesAsync();
            var counts = devices.Where(d => d.GroupId is not null)
                .GroupBy(d => d.GroupName ?? "").ToDictionary(g => g.Key, g => g.Count());
            _list.Items.Clear();
            foreach (var g in groups)
            {
                var item = new ListViewItem(g.Name) { Tag = g };
                item.SubItems.Add(g.ConsentRequired ? "igen" : "—");
                item.SubItems.Add(g.UnattendedAllowed ? "igen" : "nem");
                item.SubItems.Add(counts.TryGetValue(g.Name, out var c) ? c.ToString() : "0");
                _list.Items.Add(item);
            }
            _status.Text = $"{groups.Count} csoport.";
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task NewAsync()
    {
        using var f = new GroupEditForm();
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try { await _api.CreateGroupAsync(f.Result); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task EditAsync()
    {
        if (Selected() is not { } g) return;
        using var f = new GroupEditForm(g);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try { await _api.UpdateGroupAsync(g.Id, f.Result); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        if (Selected() is not { } g) return;
        if (MessageBox.Show($"Törlöd a(z) „{g.Name}” csoportot?\n\nA benne lévő gépek csoport nélkülivé válnak (nem törlődnek).",
                "Csoport törlése", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteGroupAsync(g.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
