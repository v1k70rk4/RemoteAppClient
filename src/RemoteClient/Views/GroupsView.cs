using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Device groups: list plus in-window editor (Back | General). Search+refresh at top,
/// Edit+Delete below the table on the right, New group below on the left.
/// </summary>
public sealed class GroupsView : UserControl, IContentView
{
    private readonly AdminApi _api;

    private readonly Panel _listHost = new() { Dock = DockStyle.Fill };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly MaterialTextBox2 _search = new() { Hint = L.GroupsView_SearchGroupName, Width = 360 };
    private readonly List<GroupInfo> _groups = new();
    private Dictionary<string, int> _counts = new();

    // Editor
    private readonly MaterialButton _tabGeneral = new() { Text = L.ChannelsView_General, AutoSize = true, Margin = new Padding(4, 0, 0, 0), Type = MaterialButton.MaterialButtonType.Contained };
    private readonly MaterialLabel _editorTitle = new() { Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Margin = new Padding(12, 10, 0, 0) };
    private readonly Panel _tabContent = new() { Dock = DockStyle.Fill };
    private GroupGeneralPanel? _generalPanel;

    public GroupsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
        ApplyTheme();
    }

    private void BuildList()
    {
        var tools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 16, 0);
        _search.TextChanged += (_, _) => RenderList();
        tools.Controls.Add(_search);
        var refresh = ViewUi.ToolbarButton(L.AboutView_Refresh, primary: false);
        refresh.Click += async (_, _) => await RefreshAsync();
        tools.Controls.Add(refresh);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.BootstrapView_Group, 220);
        _list.Columns.Add(L.GroupsView_ConsentRequired, 110);
        _list.Columns.Add("Unattended", 110);
        _list.Columns.Add(L.GroupsView_Devices, 80);
        _list.DoubleClick += (_, _) => EditSelected();

        // Below table, right: Edit + Delete for selected group.
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 2) };
        var del = ViewUi.ToolbarButton(L.BootstrapView_Delete, primary: false); del.Margin = new Padding(4, 0, 4, 0);
        del.Click += async (_, _) => await DeleteSelectedAsync();
        var edit = ViewUi.ToolbarButton(L.GroupsView_Edit); edit.Margin = new Padding(4, 0, 4, 0);
        edit.Click += (_, _) => EditSelected();
        actionRow.Controls.Add(del);   // right side
        actionRow.Controls.Add(edit);  // to its left

        // Below, left: create new group as a list-level action independent of selection.
        var newRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 0, 8, 4) };
        var newBtn = ViewUi.ToolbarButton(L.GroupsView_CreateNewGroup); newBtn.Margin = new Padding(4, 0, 4, 0);
        newBtn.Click += (_, _) => NewGroup();
        newRow.Controls.Add(newBtn);

        _listHost.Controls.Add(ViewUi.Rows(1, tools, _list, actionRow, newRow, ViewUi.StatusHost(_status)));
    }

    private void BuildEditor()
    {
        var back = ViewUi.ToolbarButton(L.ChannelsView_Back, primary: false);
        back.Click += async (_, _) => { ShowList(); await RefreshAsync(); };
        var tabbar = ViewUi.Toolbar();
        tabbar.Controls.AddRange([back, _tabGeneral]);
        _editorHost.Controls.Add(ViewUi.Rows(2, tabbar, _editorTitle, _tabContent));
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync() { ShowList(); await RefreshAsync(); }

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

    private GroupInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (GroupInfo)_list.SelectedItems[0].Tag!;

    private async Task RefreshAsync()
    {
        try
        {
            var groups = await _api.GetGroupsAsync();
            var devices = await _api.GetDevicesAsync();
            _counts = devices.Where(d => d.GroupId is not null)
                .GroupBy(d => d.GroupName ?? "").ToDictionary(g => g.Key, g => g.Count());
            _groups.Clear(); _groups.AddRange(groups);
            RenderList();
            _status.Text = L.Format(L.GroupsView_Groups, groups.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<GroupInfo> items = _groups;
        if (q.Length > 0) items = _groups.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var g in items)
        {
            var item = new ListViewItem(g.Name) { Tag = g };
            item.SubItems.Add(g.ConsentRequired ? L.Common_Yes : "—");
            item.SubItems.Add(g.UnattendedAllowed ? L.Common_Yes : L.DeviceGeneralPanel_No_2);
            item.SubItems.Add(_counts.TryGetValue(g.Name, out var c) ? c.ToString() : "0");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void EditSelected()
    {
        if (Selected() is not { } g) { _status.Text = L.GroupsView_SelectAGroup; return; }
        OpenEditor(g);
    }

    private void NewGroup() => OpenEditor(null);

    private void OpenEditor(GroupInfo? g)
    {
        _editorTitle.Text = g is null ? L.GroupsView_NewGroup : L.Format(L.GroupsView_Group, g.Name);
        _generalPanel?.Dispose();
        _generalPanel = new GroupGeneralPanel(_api, g);
        _generalPanel.Saved += OnSaved;
        _tabContent.Controls.Clear();
        _tabContent.Controls.Add(_generalPanel);
        ShowEditor();
    }

    private void OnSaved()
    {
        // After save: new group returns to list; edit stays open but refreshes the background list.
        if (_generalPanel?.IsNew == true) { ShowList(); _ = RefreshAsync(); }
        else _ = RefreshAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        if (Selected() is not { } g) { _status.Text = L.GroupsView_SelectAGroup; return; }
        if (MessageBox.Show(L.Format(L.GroupsView_DeleteGroupDevicesInIt, g.Name),
                L.GroupsView_DeleteGroup, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteGroupAsync(g.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
