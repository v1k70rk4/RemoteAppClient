using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Audit log for remote access and admin actions, with filters: a toolbar (event + user filters +
/// Refresh) over an owner-drawn Time/Who/Event/Device/Detail table. See design_handoff_console_redesign.</summary>
public sealed class LogView : UserControl, IContentView
{
    private readonly AdminApi _api;
    private readonly OwnerList _list = new(44);
    private readonly UiCombo _filter = new(240);
    private readonly TextField _actor = new(L.LogView_UserOptional, 200);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };

    private sealed record FilterItem(string? Key, string Name) { public override string ToString() => Name; }
    private sealed record Row(string Time, string Who, string Event, Color EvColor, string Device, string Detail);

    public LogView(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        _filter.Items.AddRange(new object[]
        {
            new FilterItem(null, L.LogPanel_All),
            new FilterItem("connect", L.AuditText_ConnectionWithConsent),
            new FilterItem("connect-auto", L.AuditText_ConnectionWithoutConsent),
            new FilterItem("access-denied", L.DevicesView_Denied),
            new FilterItem("access-locked", L.LogPanel_DisabledDevice),
            new FilterItem("device.enrolled", L.LogPanel_Enrollment),
            new FilterItem("device-update", L.AuditText_DeviceUpdated),
            new FilterItem("user-create", L.AuditText_UserCreated),
            new FilterItem("user-update", L.AuditText_UserUpdated),
            new FilterItem("rollout", "Rollout"),
            new FilterItem("msi-build", L.AuditText_MSIBuilt),
        });
        _filter.SelectedIndex = 0;
        _filter.Margin = new Padding(0, 0, 10, 0);
        _actor.Margin = new Padding(0, 0, 10, 0);

        var refresh = new UiButton(L.AboutView_Refresh, UiButton.Style.Outline);
        refresh.Click += async (_, _) => await RefreshAsync();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, WrapContents = false, BackColor = ThemeManager.Bg, Padding = new Padding(0, 6, 0, 0) };
        toolbar.Controls.AddRange([_filter, _actor, refresh]);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.LogPanel_Time, 160),
            new OwnerList.Col(L.LogPanel_Who, 130),
            new OwnerList.Col(L.LogPanel_Event, 240),
            new OwnerList.Col(L.DevicesView_Device, 160),
            new OwnerList.Col(L.LogPanel_Detail, 240));
        _list.PaintRow += (_, e) =>
        {
            var r = (Row)e.Item;
            e.Text(0, r.Time, UiFont.MonoSmall, ThemeManager.Text3);
            e.Text(1, r.Who, UiFont.Mono, ThemeManager.Text2);
            e.Text(2, r.Event, UiFont.Body, r.EvColor);
            e.Text(3, r.Device, UiFont.Mono, ThemeManager.Text2);
            e.Text(4, r.Detail, UiFont.Body, ThemeManager.Text3);
        };

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 12), BackColor = ThemeManager.Bg };
        root.Controls.Add(_list);
        root.Controls.Add(statusHost);
        root.Controls.Add(toolbar);
        Controls.Add(root);
    }

    public void ApplyTheme() { BackColor = ThemeManager.Bg; Invalidate(true); }

    public async Task OnShownAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            var key = (_filter.SelectedItem as FilterItem)?.Key;
            var actor = string.IsNullOrWhiteSpace(_actor.Value) ? null : _actor.Value.Trim();
            var rows = await _api.GetAuditAsync(action: key, actor: actor, limit: 500);
            _list.BeginUpdate();
            _list.Clear();
            foreach (var ev in rows)
            {
                var color = AuditText.IsNegative(ev.Action) ? ThemeManager.DangerFg
                    : AuditText.IsNoConsent(ev.Action) ? ThemeManager.WarnFg : ThemeManager.Text;
                _list.Add(new Row(ev.CreatedAt.LocalDateTime.ToString("g"), ev.Actor, AuditText.Hu(ev.Action), color, ev.Target ?? "—", AuditText.Detail(ev.Detail)));
            }
            _list.EndUpdate();
            _status.Text = L.Format(L.LogPanel_Entry, rows.Count);
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
