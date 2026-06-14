using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Tokenless install: bootstrap blob generation, optionally scoped to group/expiry/install limit,
/// plus issued blob management (usage, expiry, state, revoke/delete).
/// </summary>
public sealed class BootstrapView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly MaterialTextBox2 _search = new() { Hint = L.BootstrapView_SearchGroupMSIFileInstall, Width = 360 };
    private readonly MaterialComboBox _group = new() { Hint = L.BootstrapView_Group };
    private readonly MaterialComboBox _expiry = new() { Hint = L.EditTokenForm_Expiry };
    private readonly MaterialComboBox _maxUses = new() { Hint = L.EditTokenForm_MaxInstalls };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();
    private readonly List<BootstrapTokenInfo> _tokens = new();

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }
    private sealed record ExpiryItem(int? Hours, string Name) { public override string ToString() => Name; }
    private sealed record UsesItem(int Max, string Name) { public override string ToString() => Name; }

    public BootstrapView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _group.Width = 200; _group.Margin = new Padding(4, 0, 12, 0);
        _expiry.Width = 130; _expiry.Margin = new Padding(4, 0, 12, 0);
        _expiry.Items.AddRange([new ExpiryItem(null, L.EditTokenForm_NoExpiry), new ExpiryItem(24, L.BootstrapView_X24Hours), new ExpiryItem(168, L.BootstrapView_X7Days), new ExpiryItem(720, L.BootstrapView_X30Days)]);
        _expiry.SelectedIndex = 0;
        _maxUses.Width = 150; _maxUses.Margin = new Padding(4, 0, 12, 0);
        _maxUses.Items.AddRange([new UsesItem(100000, L.EditTokenForm_Unlimited), new UsesItem(1, L.BootstrapView_X1Install), new UsesItem(5, "5"), new UsesItem(10, "10"), new UsesItem(50, "50")]);
        _maxUses.SelectedIndex = 0;

        // Top: search and refresh.
        var topTools = ViewUi.Toolbar();
        _search.Margin = new Padding(4, 0, 16, 0);
        _search.TextChanged += (_, _) => RenderList();
        topTools.Controls.Add(_search);
        var refresh = ViewUi.ToolbarButton(L.AboutView_Refresh, primary: false);
        refresh.Click += async (_, _) => await RefreshAsync();
        topTools.Controls.Add(refresh);

        _list.View = View.Details; _list.FullRowSelect = true; _list.MultiSelect = false;
        _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.BootstrapView_InstallID, 100);
        _list.Columns.Add(L.BootstrapView_Type, 90);
        _list.Columns.Add(L.BootstrapView_Group, 120);
        _list.Columns.Add(L.BootstrapView_Used, 90);
        _list.Columns.Add(L.EditTokenForm_Expiry, 120);
        _list.Columns.Add(L.BootstrapView_Status, 90);
        _list.Columns.Add(L.BootstrapView_Created, 120);
        _list.Columns.Add(L.BootstrapView_MSIFile, 220);

        // Right below table: edit, revoke, delete, save MSI.
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 2) };
        void Act(string text, Func<Task> onClick) { var b = ViewUi.ToolbarButton(text, primary: false); b.Margin = new Padding(4, 0, 4, 0); b.Click += async (_, _) => await onClick(); actionRow.Controls.Add(b); }
        Act(L.BootstrapView_SaveMSI, SaveMsiAsync);   // jobboldalt
        Act(L.BootstrapView_Delete, DeleteAsync);
        Act(L.BootstrapView_Revoke, RevokeAsync);
        Act(L.BootstrapView_Edit, EditAsync);        // balra

        // Left below that: group, expiry, max installs, generate blob.
        _group.Width = 200; _group.Margin = new Padding(4, 0, 12, 0);
        _expiry.Width = 130; _expiry.Margin = new Padding(4, 0, 12, 0);
        _maxUses.Width = 150; _maxUses.Margin = new Padding(4, 0, 12, 0);
        var genBtn = ViewUi.ToolbarButton(L.BootstrapView_GenerateBlob);
        genBtn.Click += async (_, _) => await GenerateAsync();
        var genRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8, 0, 8, 4) };
        genRow.Controls.AddRange([_group, _expiry, _maxUses, genBtn]);

        Controls.Add(ViewUi.Rows(1, topTools, _list, actionRow, genRow, ViewUi.StatusHost(_status)));
        ApplyTheme();
    }

    public void ApplyTheme() => ThemeManager.StyleView(this, _list);

    public async Task OnShownAsync()
    {
        await LoadGroupsAsync();
        await RefreshAsync();
    }

    private async Task LoadGroupsAsync()
    {
        try
        {
            var sel = (_group.SelectedItem as GroupItem)?.Id;
            _group.Items.Clear();
            _group.Items.Add(new GroupItem(null, L.BootstrapView_NoGroup));
            foreach (var g in await _api.GetGroupsAsync()) _group.Items.Add(new GroupItem(g.Id, g.Name));
            _group.SelectedIndex = 0;
            for (int i = 0; i < _group.Items.Count; i++)
                if (_group.Items[i] is GroupItem gi && gi.Id == sel) { _group.SelectedIndex = i; break; }
        }
        catch (Exception ex) { _status.Text = L.BootstrapView_GroupsError + ex.Message; }
    }

    private async Task GenerateAsync()
    {
        try
        {
            var groupId = (_group.SelectedItem as GroupItem)?.Id;
            var hours = (_expiry.SelectedItem as ExpiryItem)?.Hours;
            var max = (_maxUses.SelectedItem as UsesItem)?.Max ?? 100000;
            var blob = await _api.CreateBootstrapAsync(max, hours, groupId);
            if (string.IsNullOrWhiteSpace(blob)) { _status.Text = L.BootstrapView_EmptyResponse; return; }
            try { Clipboard.SetText(blob); } catch { }
            MessageBox.Show(
                L.BootstrapView_BootstrapBlobCopiedToClipboard + blob +
                L.BootstrapView_InstallOnTheCustomerMachine,
                "Bootstrap blob", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _status.Text = L.BootstrapView_BlobGeneratedAndCopiedTo;
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private BootstrapTokenInfo? Selected() => _list.SelectedItems.Count == 0 ? null : (BootstrapTokenInfo)_list.SelectedItems[0].Tag!;

    private static string StateOf(BootstrapTokenInfo t) =>
        t.RevokedAt is not null ? "Visszavonva"
        : t.ExpiresAt is { } e && e < DateTimeOffset.UtcNow ? L.BootstrapView_Expired
        : t.UseCount >= t.MaxUses ? "Elfogyott"
        : L.BootstrapView_Active;

    /// <summary>Blob origin: generated for MSI, manual blob, or manual one-time token.</summary>
    private static string KindOf(BootstrapTokenInfo t) =>
        !string.IsNullOrWhiteSpace(t.MsiFileName) || t.Note is "msi-bootstrap" ? "MSI"
        : t.Note is "bootstrap" ? L.BootstrapView_ManualBlob
        : L.BootstrapView_ManualToken;

    private async Task RefreshAsync()
    {
        try
        {
            var tokens = await _api.GetTokensAsync();
            _tokens.Clear(); _tokens.AddRange(tokens);
            RenderList();
            _status.Text = $"{tokens.Count} blob.";
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private void RenderList()
    {
        var q = _search.Text.Trim();
        IEnumerable<BootstrapTokenInfo> items = _tokens;
        if (q.Length > 0)
            items = _tokens.Where(t =>
                (t.GroupName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.MsiFileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                t.Id.ToString("N").StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                KindOf(t).Contains(q, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var t in items)
        {
            var item = new ListViewItem(t.Id.ToString("N")[..8]) { Tag = t };
            item.SubItems.Add(KindOf(t));
            item.SubItems.Add(t.GroupName ?? "—");
            item.SubItems.Add($"{t.UseCount} / {(t.MaxUses >= 100000 ? "∞" : t.MaxUses.ToString())}");
            item.SubItems.Add(t.ExpiresAt?.LocalDateTime.ToString("g") ?? "—");
            item.SubItems.Add(StateOf(t));
            item.SubItems.Add(t.CreatedAt.LocalDateTime.ToString("g"));
            item.SubItems.Add(t.MsiFileName ?? "—");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private async Task EditAsync()
    {
        if (Selected() is not { } t) return;
        using var f = new EditTokenForm(t);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (f.MaxUses is null && f.ExpiresInHours is null && !f.ClearExpiry) { _status.Text = L.BootstrapView_NoChanges; return; }
        try
        {
            await _api.EditTokenAsync(t.Id, f.MaxUses, f.ExpiresInHours, f.ClearExpiry);
            _status.Text = L.BootstrapView_BlobUpdated;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message == "max_below_used"
                ? L.BootstrapView_MaxInstallsCannotBeLower
                : L.ForgotPasswordForm_Error + ex.Message;
        }
    }

    private async Task SaveMsiAsync()
    {
        if (Selected() is not { } t) return;
        if (string.IsNullOrWhiteSpace(t.MsiFileName)) { _status.Text = L.BootstrapView_ThisBlobHasNoMSI; return; }
        using var sfd = new SaveFileDialog { FileName = t.MsiFileName, Filter = L.BootstrapView_MSIInstallerMsiMsi, Title = L.BootstrapView_SaveMSI_2 };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _status.Text = L.BootstrapView_DownloadingMSI;
            await _api.DownloadMsiAsync(t.MsiFileName!, sfd.FileName);
            _status.Text = "MSI mentve: " + sfd.FileName;
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RevokeAsync()
    {
        if (Selected() is not { } t) return;
        if (t.RevokedAt is not null) { _status.Text = L.BootstrapView_AlreadyRevoked; return; }
        if (MessageBox.Show(L.Format(L.BootstrapView_RevokeThisBlobInstallID, t.Id.ToString("N")[..8]),
                L.BootstrapView_RevokeBlob, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.RevokeTokenAsync(t.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        if (Selected() is not { } t) return;
        if (MessageBox.Show(L.Format(L.BootstrapView_PermanentlyDeleteThisBlobInstall, t.Id.ToString("N")[..8]),
                L.BootstrapView_DeleteBlob, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteTokenAsync(t.Id); await RefreshAsync(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
