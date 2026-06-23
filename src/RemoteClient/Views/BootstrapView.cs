using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Tokenless install: a generate card (group / expiry / install limit + Generate blob) plus an owner-drawn
/// table of issued blobs (Type chip + Status dot) with Edit / Save MSI / Revoke / Delete actions.
/// See design_handoff_console_redesign.
/// </summary>
public sealed class BootstrapView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly TextField _search = new(L.BootstrapView_SearchGroupMSIFileInstall, 360, false, "search");
    private readonly UiCombo _group = new(180);
    private readonly UiCombo _expiry = new(140);
    private readonly UiCombo _maxUses = new(140);
    private readonly OwnerList _list = new(46);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private readonly List<BootstrapTokenInfo> _tokens = new();

    private sealed record GroupItem(Guid? Id, string Name) { public override string ToString() => Name; }
    private sealed record ExpiryItem(int? Hours, string Name) { public override string ToString() => Name; }
    private sealed record UsesItem(int Max, string Name) { public override string ToString() => Name; }

    public BootstrapView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        _expiry.Items.AddRange([new ExpiryItem(null, L.EditTokenForm_NoExpiry), new ExpiryItem(24, L.BootstrapView_X24Hours), new ExpiryItem(168, L.BootstrapView_X7Days), new ExpiryItem(720, L.BootstrapView_X30Days)]);
        _expiry.SelectedIndex = 0;
        _maxUses.Items.AddRange([new UsesItem(100000, L.EditTokenForm_Unlimited), new UsesItem(1, L.BootstrapView_X1Install), new UsesItem(5, "5"), new UsesItem(10, "10"), new UsesItem(50, "50")]);
        _maxUses.SelectedIndex = 0;

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 12), BackColor = ThemeManager.Bg };

        // --- toolbar (search + refresh) ---
        _search.Location = new Point(0, 8);
        _search.Changed += (_, _) => RenderList();
        var refresh = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline);
        refresh.Click += async (_, _) => await RefreshAsync();
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = ThemeManager.Bg };
        toolbar.Controls.Add(_search);
        toolbar.Controls.Add(refresh);
        toolbar.Resize += (_, _) => refresh.Location = new Point(toolbar.Width - refresh.Width, 8);

        // --- list ---
        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.BootstrapView_InstallID, 110),
            new OwnerList.Col(L.BootstrapView_Type, 116),
            new OwnerList.Col(L.BootstrapView_Group, 120),
            new OwnerList.Col(L.BootstrapView_Used, 92),
            new OwnerList.Col(L.EditTokenForm_Expiry, 140),
            new OwnerList.Col(L.BootstrapView_Status, 120),
            new OwnerList.Col(L.BootstrapView_MSIFile, 220));
        _list.PaintRow += PaintBlobRow;
        _list.RowActivated += _ => _ = EditAsync();

        // --- action buttons under the table ---
        var actionRow = BuildActions();

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        root.Controls.Add(_list);
        root.Controls.Add(actionRow);
        root.Controls.Add(statusHost);
        root.Controls.Add(toolbar);
        root.Controls.Add(BuildGenerateCard());

        Controls.Add(root);
        ApplyTheme();
    }

    private Card BuildGenerateCard()
    {
        var genBtn = new UiButton(L.BootstrapView_GenerateBlob, UiButton.Style.Filled, "plus");
        genBtn.Click += async (_, _) => await GenerateAsync();

        var body = new Panel();
        _group.Location = new Point(0, 24);
        _expiry.Location = new Point(196, 24);
        _maxUses.Location = new Point(346, 24);
        genBtn.Location = new Point(500, 24);
        body.Controls.Add(_group);
        body.Controls.Add(_expiry);
        body.Controls.Add(_maxUses);
        body.Controls.Add(genBtn);
        body.Paint += (_, e) =>
        {
            void Lbl(string t, int x) => TextRenderer.DrawText(e.Graphics, t, UiFont.Label, new Rectangle(x, 2, 180, 16), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);
            Lbl(L.BootstrapView_Group, 0);
            Lbl(L.EditTokenForm_Expiry, 196);
            Lbl(L.EditTokenForm_MaxInstalls, 346);
        };
        return new Card(L.BootstrapView_GenerateTitle, L.BootstrapView_GenerateDesc, body) { Dock = DockStyle.Top, Height = 156, Margin = new Padding(0, 0, 0, 14) };
    }

    private Panel BuildActions()
    {
        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = ThemeManager.Bg };
        var edit = new UiButton(L.BootstrapView_Edit, UiButton.Style.Outline) { Location = new Point(0, 8) };
        edit.Click += async (_, _) => await EditAsync();
        var saveMsi = new UiButton(L.BootstrapView_SaveMSI, UiButton.Style.Outline) { Location = new Point(edit.Width + 8, 8) };
        saveMsi.Click += async (_, _) => await SaveMsiAsync();
        var revoke = new UiButton(L.BootstrapView_Revoke, UiButton.Style.Warn);
        revoke.Click += async (_, _) => await RevokeAsync();
        var del = new UiButton(L.BootstrapView_Delete, UiButton.Style.Danger);
        del.Click += async (_, _) => await DeleteAsync();
        actions.Controls.AddRange([edit, saveMsi, revoke, del]);
        actions.Resize += (_, _) =>
        {
            del.Location = new Point(actions.Width - del.Width, 8);
            revoke.Location = new Point(del.Left - 8 - revoke.Width, 8);
        };
        return actions;
    }

    private void PaintBlobRow(object? sender, RowPaintEventArgs e)
    {
        var t = (BootstrapTokenInfo)e.Item;
        e.Text(0, t.Id.ToString("N")[..8], UiFont.MonoSemi, ThemeManager.Text);

        var (kindFg, kindBg) = KindColor(t);
        UiPaint.DrawPill(e.G, e.Cell(1).Left, e.Cy, KindOf(t), kindFg, kindBg, UiFont.Label, false);

        e.Text(2, t.GroupName ?? "—", UiFont.Body, ThemeManager.Text2);
        e.Text(3, $"{t.UseCount} / {(t.MaxUses >= 100000 ? "∞" : t.MaxUses.ToString())}", UiFont.Mono, ThemeManager.Text2);
        e.Text(4, t.ExpiresAt?.LocalDateTime.ToString("g") ?? "—", UiFont.MonoSmall, ThemeManager.Text3);

        var (stText, stColor) = State(t);
        var c5 = e.Cell(5);
        using (var b = new SolidBrush(stColor)) e.G.FillEllipse(b, c5.Left, e.Cy - 3, 7, 7);
        TextRenderer.DrawText(e.G, stText, UiFont.BodySemi, new Rectangle(c5.Left + 14, c5.Top, c5.Width - 14, c5.Height), stColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        e.Text(6, t.MsiFileName ?? "—", UiFont.MonoSmall, ThemeManager.Text2);
    }

    public void ApplyTheme() { BackColor = ThemeManager.Bg; Invalidate(true); }

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
                L.BootstrapView_BootstrapBlobCopiedToClipboard + blob + L.BootstrapView_InstallOnTheCustomerMachine,
                "Bootstrap blob", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _status.Text = L.BootstrapView_BlobGeneratedAndCopiedTo;
            await RefreshAsync();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private BootstrapTokenInfo? Selected() => _list.Selected as BootstrapTokenInfo;

    private static (string Text, Color Color) State(BootstrapTokenInfo t)
    {
        if (t.RevokedAt is not null) return ("Visszavonva", ThemeManager.Text3);
        if (t.ExpiresAt is { } e && e < DateTimeOffset.UtcNow) return (L.BootstrapView_Expired, ThemeManager.DangerFg);
        if (t.UseCount >= t.MaxUses) return ("Elfogyott", ThemeManager.WarnFg);
        return (L.BootstrapView_Active, ThemeManager.OkFg);
    }

    /// <summary>Blob origin: generated for MSI, manual blob, or manual one-time token.</summary>
    private static string KindOf(BootstrapTokenInfo t) =>
        !string.IsNullOrWhiteSpace(t.MsiFileName) || t.Note is "msi-bootstrap" ? "MSI"
        : t.Note is "bootstrap" ? L.BootstrapView_ManualBlob
        : L.BootstrapView_ManualToken;

    private static (Color Fg, Color Bg) KindColor(BootstrapTokenInfo t) =>
        !string.IsNullOrWhiteSpace(t.MsiFileName) || t.Note is "msi-bootstrap" ? (ThemeManager.Accent, ThemeManager.AccentSoft)
        : (ThemeManager.Text2, ThemeManager.Panel3);

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
        var q = _search.Query;
        IEnumerable<BootstrapTokenInfo> items = _tokens;
        if (q.Length > 0)
            items = _tokens.Where(t =>
                (t.GroupName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.MsiFileName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                t.Id.ToString("N").StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                KindOf(t).Contains(q, StringComparison.OrdinalIgnoreCase));

        _list.BeginUpdate();
        _list.Clear();
        foreach (var t in items) _list.Add(t);
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
            _status.Text = ex.Message == "max_below_used" ? L.BootstrapView_MaxInstallsCannotBeLower : L.ForgotPasswordForm_Error + ex.Message;
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
