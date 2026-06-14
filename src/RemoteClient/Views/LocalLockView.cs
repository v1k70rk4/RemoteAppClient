using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Local VNC lock: remote access can be disabled on this device through UAC and unlocked only locally.</summary>
public sealed class LocalLockView : UserControl, IContentView
{
    public Task OnShownAsync() { Refresh2(); return Task.CompletedTask; }
    public void ApplyTheme() { ThemeManager.StyleView(this); Refresh2(); }

    private readonly MaterialLabel _state = new();
    private readonly MaterialButton _toggle = new();
    private readonly MaterialLabel _status = new();

    public LocalLockView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24, 18, 24, 12);

        var title = new MaterialLabel { Text = L.LocalLockView_LocalLockDisableRemoteAccess, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        var help = new MaterialLabel
        {
            Text = L.LocalLockView_IfYouDisableThisNobody,
            AutoSize = true, MaximumSize = new Size(760, 0), Dock = DockStyle.Top,
        };
        _state.Font = new Font("Segoe UI", 12F, FontStyle.Bold); _state.AutoSize = true; _state.Dock = DockStyle.Top; _state.Margin = new Padding(0, 8, 0, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 6, 0, 6) };
        _toggle.Click += (_, _) => Toggle();
        buttons.Controls.Add(_toggle);

        // Status message as a plain label; empty is invisible, no gray card.
        _status.AutoSize = true; _status.Dock = DockStyle.Top; _status.Margin = new Padding(0, 10, 0, 0);

        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(_state);
        Controls.Add(help);
        Controls.Add(title);
        Refresh2();
    }

    private void Refresh2()
    {
        bool locked = LocalVncLock.IsLocked();
        _state.Text = locked ? L.LocalLockView_StatusDISABLEDNotRemotelyAccessible : L.LocalLockView_StatusEnabled;
        _state.ForeColor = locked ? Color.IndianRed : Color.MediumSeaGreen;
        _toggle.Text = locked ? L.LocalLockView_UnlockUAC : L.LocalLockView_DisableUAC;
    }

    private void Toggle()
    {
        bool locked = LocalVncLock.IsLocked();
        var q = locked
            ? L.LocalLockView_UnlockRemoteAccessVNCOn
            : L.LocalLockView_DisableRemoteAccessVNCOn;
        if (MessageBox.Show(q, L.LocalLockView_LocalVNCLock, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            if (LocalVncLock.RunElevated(!locked))
            {
                _status.Text = locked ? L.LocalLockView_LocalDeviceUnlocked : L.LocalLockView_LocalDeviceLOCKEDNotRemotely;
                Refresh2();
            }
            else _status.Text = L.LocalLockView_TheOperationDidNotFinish;
        }
        catch (Exception ex) { _status.Text = L.LocalLockView_LocalLockError + ex.Message; }
    }
}
