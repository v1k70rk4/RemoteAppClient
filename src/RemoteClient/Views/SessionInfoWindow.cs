using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Narrow session side panel pinned to the top-right of the operator's screen for the duration of a
/// VNC session: an editable device note on top, live-refreshing telemetry below. Opened just before
/// the viewer launches so the machine's note and state sit next to the remote desktop.
/// </summary>
public sealed class SessionInfoWindow : MaterialForm
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly Guid? _groupId;
    private readonly MaterialMultiLineTextBox2 _note = new();
    private readonly MaterialLabel _noteStatus = new() { AutoSize = true, ForeColor = Color.Gray };
    private readonly DeviceTelemetryPanel _telemetry;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30000 };
    private bool _refreshing;

    public SessionInfoWindow(AdminApi api, DeviceInfo d, Rectangle area, int width, bool keepOnTop)
    {
        _api = api; _deviceId = d.DeviceId; _groupId = d.GroupId;
        ThemeManager.Skin.AddFormToManage(this);
        Text = string.IsNullOrWhiteSpace(d.Hostname) ? d.DeviceId : d.Hostname;
        Sizable = true;
        // Split view: float beside the viewer (always on top). Background view: sit behind the
        // full-width viewer but stay reachable from the taskbar.
        ShowInTaskbar = !keepOnTop;
        TopMost = keepOnTop;
        MinimumSize = new Size(240, 280);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(area.Right - width, area.Top, width, area.Height);

        _telemetry = new DeviceTelemetryPanel(d) { Dock = DockStyle.Fill };

        // Editable note on top — operators jot notes here during a session. Dock=Top so the box
        // always follows the panel width (no overflow when the window is narrow or resized).
        var notePanel = new Panel { Dock = DockStyle.Top, Height = 214, Padding = new Padding(10, 6, 10, 6) };
        var noteHeader = new MaterialLabel { Text = L.DeviceGeneralPanel_Note, FontType = MaterialSkinManager.fontType.Subtitle2, AutoSize = true, Dock = DockStyle.Top };
        _note.Text = d.Note ?? "";
        _note.Dock = DockStyle.Top;
        _note.Height = 128;
        var noteButtons = new Panel { Dock = DockStyle.Top, Height = 44 };
        var save = new MaterialButton { Text = L.EditTokenForm_Save, AutoSize = false, Width = 96, Location = new Point(0, 6), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        save.Click += async (_, _) => await SaveNoteAsync();
        _noteStatus.AutoSize = true; _noteStatus.Location = new Point(104, 14);
        noteButtons.Controls.Add(save);
        noteButtons.Controls.Add(_noteStatus);
        // Added in reverse: with Dock=Top the last-added control sits at the very top.
        notePanel.Controls.Add(noteButtons);
        notePanel.Controls.Add(_note);
        notePanel.Controls.Add(noteHeader);

        Controls.Add(_telemetry);                                       // Fill (added first)
        Controls.Add(new MaterialDivider { Dock = DockStyle.Top });
        Controls.Add(notePanel);                                        // Top (added last -> very top)

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        FormClosed += (_, _) => { _timer.Stop(); _timer.Dispose(); };
    }

    private async Task SaveNoteAsync()
    {
        try
        {
            // Preserve the group (the General tab sends Empty for "no group"); only the note changes here.
            await _api.UpdateDeviceAsync(_deviceId, new DeviceUpdate { GroupId = _groupId ?? Guid.Empty, Note = _note.Text });
            _noteStatus.Text = L.Common_Saved;
        }
        catch (Exception ex) { _noteStatus.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var list = await _api.GetDevicesAsync();
            var nd = list.FirstOrDefault(x => x.DeviceId == _deviceId);
            if (nd is not null && !IsDisposed) _telemetry.Update(nd); // note textbox is left as-is so edits are not clobbered
        }
        catch { /* transient; try again next tick */ }
        finally { _refreshing = false; }
    }
}
