using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Local locks: VNC (remote access) and file transfer can each be disabled on this device through UAC,
/// independently of each other, and unlocked only locally. Left button = VNC, right button = file transfer.
/// </summary>
public sealed class LocalLockView : UserControl, IContentView
{
    public Task OnShownAsync() { Refresh2(); return Task.CompletedTask; }
    public void ApplyTheme() { ThemeManager.StyleView(this); Refresh2(); }

    private readonly MaterialLabel _vncState = new();
    private readonly MaterialLabel _fileState = new();
    private readonly MaterialButton _vncToggle = new();
    private readonly MaterialButton _fileToggle = new();
    private readonly MaterialLabel _status = new();

    public LocalLockView()
    {
        Dock = DockStyle.Fill;
        Padding = new Padding(24, 18, 24, 12);

        var title = new MaterialLabel { Text = L.LocalLockView_LocalLockDisableRemoteAccess, Font = new Font("Segoe UI", 13F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        var help = new MaterialLabel { Text = L.LocalLockView_IfYouDisableThisNobody, AutoSize = true, MaximumSize = new Size(760, 0), Dock = DockStyle.Top };

        _vncState.Font = new Font("Segoe UI", 12F, FontStyle.Bold); _vncState.AutoSize = true; _vncState.Dock = DockStyle.Top; _vncState.Margin = new Padding(0, 10, 0, 2);
        _fileState.Font = new Font("Segoe UI", 12F, FontStyle.Bold); _fileState.AutoSize = true; _fileState.Dock = DockStyle.Top; _fileState.Margin = new Padding(0, 6, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 8, 0, 6) };
        _vncToggle.Click += (_, _) => ToggleVnc();
        _fileToggle.Click += (_, _) => ToggleFile();
        _vncToggle.Margin = new Padding(0, 6, 0, 6);
        _fileToggle.Margin = new Padding(12, 6, 0, 6);
        buttons.Controls.Add(_vncToggle);   // left = VNC
        buttons.Controls.Add(_fileToggle);  // right = file transfer

        _status.AutoSize = true; _status.Dock = DockStyle.Top; _status.Margin = new Padding(0, 10, 0, 0);

        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(_fileState);
        Controls.Add(_vncState);
        Controls.Add(help);
        Controls.Add(title);
        Refresh2();
    }

    private void Refresh2()
    {
        bool vnc = LocalVncLock.IsLocked();
        bool file = LocalFileLock.IsLocked();
        _vncState.Text = $"{L.LocalLockView_Vnc}:  " + (vnc ? L.LocalLockView_StatusDISABLEDNotRemotelyAccessible : L.LocalLockView_StatusEnabled);
        _vncState.ForeColor = vnc ? Color.IndianRed : Color.MediumSeaGreen;
        _fileState.Text = $"{L.LocalLockView_FileTransfer}:  " + (file ? L.LocalLockView_StatusDISABLEDNotRemotelyAccessible : L.LocalLockView_StatusEnabled);
        _fileState.ForeColor = file ? Color.IndianRed : Color.MediumSeaGreen;
        _vncToggle.Text = $"{L.LocalLockView_Vnc} — " + (vnc ? L.LocalLockView_UnlockUAC : L.LocalLockView_DisableUAC);
        _fileToggle.Text = $"{L.LocalLockView_FileTransfer} — " + (file ? L.LocalLockView_UnlockUAC : L.LocalLockView_DisableUAC);
    }

    private void ToggleVnc()
    {
        bool locked = LocalVncLock.IsLocked();
        var q = locked ? L.LocalLockView_UnlockRemoteAccessVNCOn : L.LocalLockView_DisableRemoteAccessVNCOn;
        if (MessageBox.Show(q, L.LocalLockView_LocalVNCLock, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        RunToggle(() => LocalVncLock.RunElevated(!locked), locked);
    }

    private void ToggleFile()
    {
        bool locked = LocalFileLock.IsLocked();
        var q = locked ? L.LocalLockView_UnlockFileTransferOn : L.LocalLockView_DisableFileTransferOn;
        if (MessageBox.Show(q, L.LocalLockView_LocalFileLock, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        RunToggle(() => LocalFileLock.RunElevated(!locked), locked);
    }

    private void RunToggle(Func<bool> op, bool wasLocked)
    {
        try
        {
            if (op())
            {
                _status.Text = wasLocked ? L.LocalLockView_LocalDeviceUnlocked : L.LocalLockView_LocalDeviceLOCKEDNotRemotely;
                Refresh2();
            }
            else _status.Text = L.LocalLockView_TheOperationDidNotFinish;
        }
        catch (Exception ex) { _status.Text = L.LocalLockView_LocalLockError + ex.Message; }
    }
}
