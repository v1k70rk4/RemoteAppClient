using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Device groups: redesigned list (search + Create new group + owner-drawn card table) plus an in-window
/// editor (back + title + GroupGeneralPanel card). See design_handoff_console_redesign.
/// </summary>
public sealed class GroupsView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly Panel _listHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 12) };
    private readonly Panel _editorHost = new() { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 18), Visible = false };
    private readonly OwnerList _list = new(50);
    private readonly TextField _search = new(L.GroupsView_SearchGroupName, 340, false, "search");
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private readonly List<GroupInfo> _groups = new();
    private Dictionary<string, int> _counts = new();

    private readonly Panel _editorBody = new() { Dock = DockStyle.Fill };
    private readonly IconButton _back = new("chevron");
    private string _editorTitle = "";
    private GroupGeneralPanel? _generalPanel;

    public GroupsView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        BuildList();
        BuildEditor();
        Controls.Add(_editorHost);
        Controls.Add(_listHost);
    }

    private void BuildList()
    {
        _search.Location = new Point(0, 8);
        _search.Changed += (_, _) => RenderList();

        var newBtn = new UiButton(L.GroupsView_CreateNewGroup, UiButton.Style.Filled, "plus");
        newBtn.Click += (_, _) => OpenEditor(null);

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = ThemeManager.Bg };
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(newBtn);
        toolbar.Resize += (_, _) => newBtn.Location = new Point(toolbar.Width - newBtn.Width, 8);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.BootstrapView_Group, 240),
            new OwnerList.Col(L.GroupsView_ConsentRequired, 160),
            new OwnerList.Col("Unattended", 130),
            new OwnerList.Col(L.GroupsView_Devices, 90, Right: true));
        _list.PaintRow += PaintGroupRow;
        _list.RowActivated += item => OpenEditor((GroupInfo)item);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        _listHost.Controls.Add(_list);
        _listHost.Controls.Add(statusHost);
        _listHost.Controls.Add(toolbar);
    }

    private void PaintGroupRow(object? sender, RowPaintEventArgs e)
    {
        var grp = (GroupInfo)e.Item;
        var c0 = e.Cell(0);
        var tile = new Rectangle(c0.Left, e.Cy - 15, 30, 30);
        UiPaint.FillRoundedRect(e.G, tile, 8, ThemeManager.Panel3);
        UiIcons.Draw(e.G, "layers", new RectangleF(tile.X + 7, tile.Y + 7, 16, 16), ThemeManager.Text2);
        TextRenderer.DrawText(e.G, grp.Name, UiFont.BodySemi, new Rectangle(tile.Right + 11, c0.Top, c0.Right - tile.Right - 11, c0.Height),
            ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        e.Text(1, grp.ConsentRequired ? L.Common_Yes : "—", UiFont.Body, grp.ConsentRequired ? ThemeManager.OkFg : ThemeManager.Text3);
        e.Text(2, grp.UnattendedAllowed ? L.Common_Yes : L.DeviceGeneralPanel_No_2, UiFont.Body, grp.UnattendedAllowed ? ThemeManager.Text : ThemeManager.Text3);
        e.Text(3, _counts.TryGetValue(grp.Name, out var c) ? c.ToString() : "0", UiFont.MonoSemi, ThemeManager.Text);
    }

    private void BuildEditor()
    {
        _back.SetBounds(0, 8, 36, 36);
        _back.Click += (_, _) => { ShowList(); _ = RefreshAsync(); };
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = ThemeManager.Bg };
        header.Controls.Add(_back);
        header.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, _editorTitle, UiFont.PageTitle,
            new Rectangle(48, 0, header.Width - 48, 46), ThemeManager.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        _editorHost.Controls.Add(_editorBody);
        _editorHost.Controls.Add(header);
    }

    public void ApplyTheme()
    {
        BackColor = _listHost.BackColor = _editorHost.BackColor = ThemeManager.Bg;
        Invalidate(true);
    }

    public async Task OnShownAsync() { ShowList(); await RefreshAsync(); }

    private void ShowList() { _editorHost.Visible = false; _listHost.Visible = true; _listHost.BringToFront(); }
    private void ShowEditor() { _listHost.Visible = false; _editorHost.Visible = true; _editorHost.BringToFront(); }

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
        var q = _search.Query;
        IEnumerable<GroupInfo> items = _groups;
        if (q.Length > 0) items = _groups.Where(g => g.Name.Contains(q, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Clear();
        foreach (var g in items) _list.Add(g);
        _list.EndUpdate();
    }

    private void OpenEditor(GroupInfo? g)
    {
        _editorTitle = g is null ? L.GroupsView_NewGroup : L.Format(L.GroupsView_Group, g.Name);
        _generalPanel?.Dispose();
        _generalPanel = new GroupGeneralPanel(_api, g);
        _generalPanel.Saved += OnSaved;
        _generalPanel.Cancelled += () => { ShowList(); _ = RefreshAsync(); };
        _generalPanel.Deleted += () => { ShowList(); _ = RefreshAsync(); };
        _editorBody.Controls.Clear();
        _editorBody.Controls.Add(_generalPanel);
        ShowEditor();
        _editorHost.Invalidate(true);
    }

    private void OnSaved()
    {
        if (_generalPanel?.IsNew == true) { ShowList(); _ = RefreshAsync(); }
        else _ = RefreshAsync();
    }
}
