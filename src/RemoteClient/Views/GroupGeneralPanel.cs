using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device group General tab: name plus consent/unattended defaults and Save. Creates (id=null) or edits.</summary>
public sealed class GroupGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid? _id;
    private readonly MaterialTextBox2 _name = new() { Hint = L.GroupGeneralPanel_GroupName, Width = 360 };
    private readonly MaterialSwitch _consent = new() { Text = L.GroupGeneralPanel_ConsentRequiredForViewing, AutoSize = true };
    private readonly MaterialSwitch _unattended = new() { Text = L.GroupGeneralPanel_AllowUnattendedAccess, AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    /// <summary>Raised after successful save so the view can refresh or return.</summary>
    public event Action? Saved;

    public bool IsNew => _id is null;

    public GroupGeneralPanel(AdminApi api, GroupInfo? existing)
    {
        _api = api; _id = existing?.Id;
        Dock = DockStyle.Fill;

        if (existing is not null)
        {
            _name.Text = existing.Name; _consent.Checked = existing.ConsentRequired; _unattended.Checked = existing.UnattendedAllowed;
        }
        else { _unattended.Checked = true; }

        var save = ViewUi.ToolbarButton(L.EditTokenForm_Save);
        save.Click += async (_, _) => await SaveAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        _name.Margin = new Padding(4, 8, 4, 12);
        _consent.Margin = new Padding(4, 8, 4, 8);
        _unattended.Margin = new Padding(4, 8, 4, 12);
        body.Controls.Add(_name);
        body.Controls.Add(_consent);
        body.Controls.Add(_unattended);
        body.Controls.Add(save);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text)) { _status.Text = "Adj meg egy nevet."; return; }
        var info = new GroupInfo
        {
            Id = _id ?? Guid.Empty,
            Name = _name.Text.Trim(),
            ConsentRequired = _consent.Checked,
            UnattendedAllowed = _unattended.Checked,
        };
        try
        {
            if (_id is { } id) await _api.UpdateGroupAsync(id, info);
            else await _api.CreateGroupAsync(info);
            _status.Text = "Mentve.";
            Saved?.Invoke();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
    }
}
