using System.Drawing;
using MaterialSkin;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Narrow session side panel pinned to the top-right of the operator's screen for the duration of a
/// VNC session: the device note on top, full telemetry below. Opened just before the viewer launches
/// so the machine's note and live state sit next to the remote desktop.
/// </summary>
public sealed class SessionInfoWindow : MaterialForm
{
    public SessionInfoWindow(DeviceInfo d, Rectangle area, int width)
    {
        ThemeManager.Skin.AddFormToManage(this);
        Text = string.IsNullOrWhiteSpace(d.Hostname) ? d.DeviceId : d.Hostname;
        Sizable = true;
        ShowInTaskbar = false;
        TopMost = true;
        MinimumSize = new Size(240, 240);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(area.Right - width, area.Top, width, area.Height);

        // Telemetry fills the remaining height; the note sits on top with a divider between them.
        var telemetry = new DeviceTelemetryPanel(d) { Dock = DockStyle.Fill };

        int noteH = Math.Min(320, Math.Max(120, (int)(area.Height * 0.22)));
        var notePanel = new Panel { Dock = DockStyle.Top, Height = noteH, AutoScroll = true, Padding = new Padding(12, 8, 8, 8) };
        notePanel.Controls.Add(new MaterialLabel
        {
            Text = string.IsNullOrWhiteSpace(d.Note) ? "—" : d.Note,
            AutoSize = true, MaximumSize = new Size(width - 48, 0), Location = new Point(8, 34),
        });
        notePanel.Controls.Add(new MaterialLabel
        {
            Text = L.DeviceGeneralPanel_Note, FontType = MaterialSkinManager.fontType.Subtitle2,
            AutoSize = true, Location = new Point(8, 6),
        });

        Controls.Add(telemetry);                                        // Fill (added first)
        Controls.Add(new MaterialDivider { Dock = DockStyle.Top });
        Controls.Add(notePanel);                                        // Top (added last -> very top)
    }
}
