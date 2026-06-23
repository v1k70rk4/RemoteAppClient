using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Embedded audit log, optionally fixed to one deviceId or actor. Owner-drawn chip rows (time + a colored
/// event tag + message) inside a rounded card, per design_handoff_console_redesign. The event-type filter is
/// always available. Event keys and tag/color are localized/derived by <see cref="AuditText"/>.
/// </summary>
public sealed class LogPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string? _deviceId;
    private readonly string? _actor;
    private readonly ListView _list = new();
    private readonly UiCombo _filter = new(240);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private int _hover = -1;

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }
    private sealed record Row(string Time, string Tag, Color Fg, Color Bg, string Msg);

    public LogPanel(AdminApi api, string? deviceId = null, string? actor = null)
    {
        _api = api; _deviceId = deviceId; _actor = actor;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

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
        _filter.Margin = new Padding(0, 4, 12, 0);

        var refresh = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline) { Margin = new Padding(0, 4, 0, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();

        _list.View = View.Details;
        _list.OwnerDraw = true;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HeaderStyle = ColumnHeaderStyle.None;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = ThemeManager.Panel;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("", 100);
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, 42) }; // forces 42px row height
        _list.DrawItem += DrawRow;
        _list.DrawSubItem += (_, e) => e.DrawDefault = false;
        _list.SizeChanged += (_, _) => { if (_list.Columns.Count > 0) _list.Columns[0].Width = _list.ClientSize.Width; };
        _list.MouseMove += (_, e) => { int i = _list.GetItemAt(e.X, e.Y)?.Index ?? -1; if (i != _hover) { _hover = i; _list.Invalidate(); } };
        _list.MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; _list.Invalidate(); } };
        typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_list, true);

        var card = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Panel, Padding = new Padding(1, 6, 1, 6) };
        card.Paint += (_, e) => UiPaint.DrawCard(e.Graphics, new Rectangle(0, 0, card.Width - 1, card.Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        card.Controls.Add(_list);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, BackColor = ThemeManager.Bg };
        toolbar.Controls.Add(_filter);
        toolbar.Controls.Add(refresh);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        Controls.Add(card);
        Controls.Add(statusHost);
        Controls.Add(toolbar);
    }

    public async Task ShownAsync() => await RefreshAsync();

    private void DrawRow(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item?.Tag is not Row row) return;
        var g = e.Graphics;
        var rect = new Rectangle(e.Bounds.Left, e.Bounds.Top, _list.ClientSize.Width, e.Bounds.Height);
        using (var bg = new SolidBrush(e.ItemIndex == _hover ? ThemeManager.Panel2 : ThemeManager.Panel)) g.FillRectangle(bg, rect);

        int cy = rect.Top + rect.Height / 2;
        TextRenderer.DrawText(g, row.Time, UiFont.MonoSmall, new Rectangle(rect.Left + 18, rect.Top, 140, rect.Height),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        int chipX = rect.Left + 168;
        int chipW = UiPaint.DrawPill(g, chipX, cy, row.Tag, row.Fg, row.Bg, UiFont.Label, false);

        int msgX = chipX + chipW + 12;
        TextRenderer.DrawText(g, row.Msg, UiFont.Body, new Rectangle(msgX, rect.Top, rect.Right - msgX - 16, rect.Height),
            ThemeManager.Text2, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, rect.Left + 10, rect.Bottom - 1, rect.Right - 10, rect.Bottom - 1);
    }

    private async Task RefreshAsync()
    {
        try
        {
            var key = (_filter.SelectedItem as FilterItem)?.Key;
            var rows = await _api.GetAuditAsync(action: key, actor: _actor, deviceId: _deviceId, limit: 500);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var ev in rows)
            {
                var (tag, fg, bg) = AuditText.Chip(ev.Action);
                string msg = AuditText.Hu(ev.Action);
                if (_actor is null && !string.IsNullOrEmpty(ev.Actor)) msg += "  ·  " + ev.Actor;
                var det = AuditText.Detail(ev.Detail);
                if (det != "—") msg += "  ·  " + det;
                _list.Items.Add(new ListViewItem { Tag = new Row(ev.CreatedAt.LocalDateTime.ToString("g"), tag, fg, bg, msg) });
            }
            _list.EndUpdate();
            _status.Text = L.Format(L.LogPanel_Entry, rows.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
