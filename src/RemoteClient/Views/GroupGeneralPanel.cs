using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Group editor card: name + consent/unattended toggles + Save/Cancel (and Delete when editing).
/// Creates (id=null) or edits. See design_handoff_console_redesign (groups editor).</summary>
public sealed class GroupGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid? _id;
    private readonly TextField _name = new("", 360);
    private readonly UiToggle _consent = new();
    private readonly UiToggle _unattended = new();
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(2, 10, 0, 0) };

    /// <summary>Raised after a successful save.</summary>
    public event Action? Saved;
    /// <summary>Raised on Cancel (return to the list without saving).</summary>
    public event Action? Cancelled;
    /// <summary>Raised after the group is deleted.</summary>
    public event Action? Deleted;

    public bool IsNew => _id is null;

    public GroupGeneralPanel(AdminApi api, GroupInfo? existing)
    {
        _api = api; _id = existing?.Id;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;

        if (existing is not null)
        {
            _name.Value = existing.Name; _consent.Checked = existing.ConsentRequired; _unattended.Checked = existing.UnattendedAllowed;
        }
        else { _unattended.Checked = true; }

        var save = new UiButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();
        var cancel = new UiButton(L.FileManager_Cancel, UiButton.Style.Outline);
        cancel.Click += (_, _) => Cancelled?.Invoke();

        const int cardW = 520, contentW = cardW - 40;
        var body = new Panel();
        _name.SetBounds(0, 24, contentW, 38);
        body.Controls.Add(_name);
        body.Controls.Add(new SettingRow(L.GroupGeneralPanel_ConsentRequiredForViewing, L.GroupGeneralPanel_ConsentDesc, _consent)
            { Location = new Point(0, 78), Size = new Size(contentW, 54) });
        body.Controls.Add(new SettingRow(L.GroupGeneralPanel_AllowUnattendedAccess, L.GroupGeneralPanel_UnattendedDesc, _unattended)
            { Location = new Point(0, 132), Size = new Size(contentW, 54) });
        save.Location = new Point(0, 198);
        cancel.Location = new Point(save.Right + 10, 198);
        body.Controls.Add(save);
        body.Controls.Add(cancel);
        if (!IsNew)
        {
            var del = new UiButton(L.BootstrapView_Delete, UiButton.Style.Danger);
            del.Location = new Point(contentW - del.Width, 198);
            del.Click += async (_, _) => await DeleteAsync();
            body.Controls.Add(del);
        }
        _status.Location = new Point(2, 244);
        body.Controls.Add(_status);
        body.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, L.GroupGeneralPanel_GroupName, UiFont.Label,
            new Rectangle(0, 2, contentW, 16), ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.NoPadding);

        Controls.Add(new Card(null, null, body) { Width = cardW, Height = 300, Location = new Point(0, 0) });
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Value)) { _status.Text = L.GroupsView_SelectAGroup; return; }
        var info = new GroupInfo
        {
            Id = _id ?? Guid.Empty,
            Name = _name.Value.Trim(),
            ConsentRequired = _consent.Checked,
            UnattendedAllowed = _unattended.Checked,
        };
        try
        {
            if (_id is { } id) await _api.UpdateGroupAsync(id, info);
            else await _api.CreateGroupAsync(info);
            _status.Text = L.Common_Saved;
            Saved?.Invoke();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        if (_id is not { } id) return;
        if (MessageBox.Show(L.Format(L.GroupsView_DeleteGroupDevicesInIt, _name.Value),
                L.GroupsView_DeleteGroup, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try { await _api.DeleteGroupAsync(id); Deleted?.Invoke(); }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
