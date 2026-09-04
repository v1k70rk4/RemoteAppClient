using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// A device's history: when it moved between online / flaky / not-controllable / offline, and when its IP
/// changed. Owner-drawn chip rows (time + coloured state chip + "from → to"), matching the audit log.
///
/// The server records transitions only, so a settled device produces nothing and this list stays readable.
/// It replaced a per-minute telemetry snapshot table that grew to hundreds of megabytes and which no screen
/// in the product could ever show.
/// </summary>
public sealed class DeviceHistoryPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private int _hover = -1;

    private sealed record Row(string Time, string Tag, Color Fg, Color Bg, string Msg);

    public DeviceHistoryPanel(AdminApi api, string deviceId)
    {
        _api = api; _deviceId = deviceId;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

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
        toolbar.Controls.Add(refresh);

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 24, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        Controls.Add(card);
        Controls.Add(statusHost);
        Controls.Add(toolbar);
    }

    public async Task ShownAsync() => await RefreshAsync();

    /// <summary>Same wording and colours as the list badge, so a row in the history reads as the state the
    /// operator saw at the time.</summary>
    private static (string Text, Color Fg, Color Bg) StateChip(string? state) => state switch
    {
        "online" => (L.DevicesView_Online, ThemeManager.OkFg, ThemeManager.OkBg),
        "flaky" => (L.DevicesView_LinkFlaky, ThemeManager.WarnFg, ThemeManager.WarnBg),
        "reporting" => (L.DevicesView_ReportingOnly, ThemeManager.BetaFg, ThemeManager.BetaBg),
        _ => (L.DevicesView_Offline, ThemeManager.OffFg, ThemeManager.OffBg),
    };

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
            var rows = await _api.GetDeviceEventsAsync(_deviceId, limit: 500);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var ev in rows)
            {
                string tag; Color fg, bg; string msg;
                if (string.Equals(ev.Kind, "state", StringComparison.OrdinalIgnoreCase))
                {
                    (tag, fg, bg) = StateChip(ev.NewValue);
                    // No "from" on the first observation after a server start - do not invent a transition.
                    msg = ev.OldValue is null
                        ? StateChip(ev.NewValue).Text
                        : $"{StateChip(ev.OldValue).Text}  →  {StateChip(ev.NewValue).Text}";
                }
                else
                {
                    tag = string.Equals(ev.Kind, "public-ip", StringComparison.OrdinalIgnoreCase)
                        ? L.DeviceTelemetryPanel_PublicIP
                        : L.DeviceTelemetryPanel_IPAddressLocal;
                    fg = ThemeManager.Accent; bg = ThemeManager.AccentSoft;
                    msg = string.IsNullOrWhiteSpace(ev.OldValue) ? (ev.NewValue ?? "—") : $"{ev.OldValue}  →  {ev.NewValue}";
                }

                _list.Items.Add(new ListViewItem { Tag = new Row(ev.At.LocalDateTime.ToString("g"), tag, fg, bg, msg) });
            }
            _list.EndUpdate();
            _status.Text = rows.Count == 0 ? L.DevicesView_HistoryEmpty : L.Format(L.LogPanel_Entry, rows.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
