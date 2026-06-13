using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;

namespace RemoteClient.Views;

/// <summary>Egy eszközcsoport „Általános" füle: név + consent/unattended alapértelmezés + Mentés. Létrehozás (id=null) vagy módosítás.</summary>
public sealed class GroupGeneralPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly Guid? _id;
    private readonly MaterialTextBox2 _name = new() { Hint = "Csoport neve", Width = 360 };
    private readonly MaterialSwitch _consent = new() { Text = "Hozzájárulás kell megtekintéshez", AutoSize = true };
    private readonly MaterialSwitch _unattended = new() { Text = "Unattended (felügyelet nélküli) engedélyezve", AutoSize = true };
    private readonly MaterialLabel _status = new() { AutoSize = true, Margin = new Padding(4, 12, 0, 0) };

    /// <summary>Sikeres mentés után jelez (a nézet frissít / visszatér).</summary>
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

        var save = ViewUi.ToolbarButton("Mentés");
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
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; }
    }
}
