using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Uploads one or more exe/msi packages to a release channel — a toolbar (channel + component override +
/// Add files) over an owner-drawn queue (File / Component / Version) with Set version / Remove / Clear /
/// Upload. Component + version are read from each file but can be overridden. See design_handoff.
/// </summary>
public sealed class UploadPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly UiCombo _channel = new(120);
    private readonly UiCombo _override = new(160);
    private readonly TextField _verInput = new(L.ChannelsView_Version, 170, mono: true);
    private readonly OwnerList _list = new(42);
    private readonly MaterialLabel _status = new() { Dock = DockStyle.Fill };
    private readonly List<Pkg> _queue = new();

    /// <summary>Raised after a successful upload so the Channels view can refresh and return.</summary>
    public event Action? Uploaded;

    private sealed record Pkg(string Path, string Component, string Version);

    public UploadPanel(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(22, 8, 22, 12);

        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 1; _channel.Margin = new Padding(0, 0, 10, 0);
        _override.Items.AddRange(["(auto)", "agent", "updater", "client", "vnc"]); _override.SelectedIndex = 0; _override.Margin = new Padding(0, 0, 10, 0);
        _override.SelectedIndexChanged += (_, _) => ReapplyComponent();

        var addBtn = new UiButton(L.UploadPanel_AddFiles, UiButton.Style.Outline, "plus");
        addBtn.Click += (_, _) => AddFiles();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 50, WrapContents = false, BackColor = ThemeManager.Bg, Padding = new Padding(0, 6, 0, 0) };
        toolbar.Controls.AddRange([_channel, _override, addBtn]);

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(new OwnerList.Col(L.UploadPanel_File, 380), new OwnerList.Col(L.ChannelsView_Component, 140), new OwnerList.Col(L.ChannelsView_Version, 160));
        _list.PaintRow += (_, e) =>
        {
            var p = (Pkg)e.Item;
            e.Text(0, Path.GetFileName(p.Path), UiFont.Mono, ThemeManager.Text);
            e.Text(1, Cap(p.Component), UiFont.Body, ThemeManager.Text2);
            e.Text(2, string.IsNullOrWhiteSpace(p.Version) ? L.UploadPanel_Unknown : p.Version, UiFont.Mono, ThemeManager.Text2);
        };

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = ThemeManager.Bg };
        _verInput.Location = new Point(0, 5);
        var setVer = new UiButton(L.UploadPanel_SetVersion, UiButton.Style.Outline) { Location = new Point(_verInput.Width + 8, 6) };
        setVer.Click += (_, _) => ApplyVersion();
        var rm = new UiButton(L.UploadPanel_Remove, UiButton.Style.Outline) { Location = new Point(_verInput.Width + 8 + setVer.Width + 8, 6) };
        rm.Click += (_, _) => RemoveSelected();
        var upload = new UiButton(L.UploadPanel_Upload);
        upload.Click += async (_, _) => await UploadAsync();
        var clr = new UiButton(L.UploadPanel_ClearAll, UiButton.Style.Outline);
        clr.Click += (_, _) => { _queue.Clear(); Refill(); };
        actions.Controls.AddRange([_verInput, setVer, rm, clr, upload]);
        actions.Resize += (_, _) =>
        {
            upload.Location = new Point(actions.Width - upload.Width, 6);
            clr.Location = new Point(upload.Left - 8 - clr.Width, 6);
        };

        var statusHost = new Panel { Dock = DockStyle.Bottom, Height = 22, BackColor = ThemeManager.Bg };
        statusHost.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(actions);
        Controls.Add(statusHost);
        Controls.Add(toolbar);
    }

    private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private void Refill()
    {
        _list.BeginUpdate();
        _list.Clear();
        foreach (var p in _queue) _list.Add(p);
        _list.EndUpdate();
    }

    private void AddFiles()
    {
        using var d = new OpenFileDialog { Filter = L.UploadPanel_InstallerExeMsiExeMsi, Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in d.FileNames)
            _queue.Add(new Pkg(path, ComponentFor(path), ReadVersion(path)));
        Refill();
        _status.Text = L.Format(L.UploadPanel_FilesInTheList, _queue.Count);
    }

    private void ReapplyComponent()
    {
        for (int i = 0; i < _queue.Count; i++) _queue[i] = _queue[i] with { Component = ComponentFor(_queue[i].Path) };
        Refill();
    }

    private void ApplyVersion()
    {
        var v = _verInput.Value.Trim();
        if (v.Length == 0) { _status.Text = L.UploadPanel_EnterAVersionForExample; return; }
        if (_list.Selected is not Pkg sel) return;
        int i = _queue.IndexOf(sel);
        if (i >= 0) { _queue[i] = sel with { Version = v }; Refill(); }
    }

    private void RemoveSelected()
    {
        if (_list.Selected is Pkg sel) { _queue.Remove(sel); Refill(); }
    }

    private string ComponentFor(string path)
    {
        if (_override.SelectedItem is string o && o != "(auto)") return o;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (ext == ".msi" || n.Contains("tightvnc") || n.Contains("vnc")) return "vnc";
        if (n.Contains("updater")) return "updater";
        if (n.Contains("client")) return "client";
        return "agent";
    }

    private static string ReadVersion(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            var s = v.FileVersion;
            return string.IsNullOrWhiteSpace(s) ? $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}.{v.FilePrivatePart}" : s.Trim();
        }
        catch { return ""; }
    }

    private async Task UploadAsync()
    {
        if (_queue.Count == 0) { _status.Text = L.UploadPanel_AddAtLeastOneExe; return; }
        var channel = (string)_channel.SelectedItem!;
        try
        {
            Enabled = false;
            int done = 0;
            foreach (var p in _queue)
            {
                if (string.IsNullOrWhiteSpace(p.Version)) { _status.Text = L.Format(L.UploadPanel_NoVersionSkipped, Path.GetFileName(p.Path)); continue; }
                _status.Text = L.Format(L.UploadPanel_Upload_2, done + 1, _queue.Count, Path.GetFileName(p.Path), channel, p.Component, p.Version);
                await _api.UploadPackageAsync(channel, p.Component, p.Version, p.Path);
                done++;
            }
            _status.Text = L.Format(L.UploadPanel_PackagesUploaded, done);
            Enabled = true;
            if (done > 0) Uploaded?.Invoke();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; Enabled = true; }
    }
}
