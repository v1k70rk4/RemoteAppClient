using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>
/// Uploads one or more exe/msi packages to a release channel as an embedded panel in the
/// Channels upload tab. Component and version are read from the file but can be overridden;
/// bulk upload is supported.
/// </summary>
public sealed class UploadPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly MaterialComboBox _channel = new() { Hint = L.DeviceTelemetryPanel_014 };
    private readonly MaterialComboBox _override = new() { Hint = L.ChannelsView_014 };
    private readonly MaterialTextBox2 _verInput = new() { Hint = L.ChannelsView_004, Width = 120 };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    /// <summary>Raised after successful upload so the Channels view can refresh and return.</summary>
    public event Action? Uploaded;

    private sealed record Pkg(string Path, string Component, string Version);

    public UploadPanel(AdminApi api)
    {
        _api = api;
        Dock = DockStyle.Fill;

        _channel.Width = 120; _channel.Margin = new Padding(4, 0, 12, 0);
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 1;
        _override.Width = 150; _override.Margin = new Padding(4, 0, 12, 0);
        _override.Items.AddRange(["(auto)", "agent", "updater", "client", "vnc"]); _override.SelectedIndex = 0;
        _override.SelectedIndexChanged += (_, _) => ReapplyComponent();
        _verInput.Margin = new Padding(4, 0, 4, 0);

        var addBtn = new MaterialButton { Text = L.UploadPanel_001, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        addBtn.Click += (_, _) => AddFiles();

        // Top: channel, component, add files.
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(10, 8, 10, 6) };
        toolbar.Controls.AddRange([_channel, _override, addBtn]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add(L.UploadPanel_002, 320);
        _list.Columns.Add(L.ChannelsView_014, 120);
        _list.Columns.Add(L.ChannelsView_004, 180);

        // Right below table, first row: version, set version, remove.
        var rmBtn = new MaterialButton { Text = L.UploadPanel_003, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        rmBtn.Click += (_, _) => { foreach (ListViewItem it in _list.SelectedItems) it.Remove(); };
        var setVerBtn = new MaterialButton { Text = L.UploadPanel_004, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        setVerBtn.Click += (_, _) => ApplyVersion();
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 8, 2) };
        row1.Controls.AddRange([rmBtn, setVerBtn, _verInput]); // RightToLeft -> left-to-right: version | set version | remove

        // Second row: clear all, upload.
        var clrBtn = new MaterialButton { Text = L.UploadPanel_005, AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        clrBtn.Click += (_, _) => _list.Items.Clear();
        var uploadBtn = new MaterialButton { Text = L.UploadPanel_006, AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        uploadBtn.Click += async (_, _) => await UploadAsync();
        var row2 = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 0, 8, 4) };
        row2.Controls.AddRange([uploadBtn, clrBtn]); // RightToLeft -> left-to-right: clear all | upload

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 28 };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(12, 0, 12, 0);
        statusPanel.Controls.Add(_status);

        // Dock order: Fill, then Bottom rows from top to bottom, then Top.
        Controls.Add(_list);
        Controls.Add(row1);
        Controls.Add(row2);
        Controls.Add(statusPanel);
        Controls.Add(toolbar);
        ThemeManager.StyleList(_list);
    }

    private void AddFiles()
    {
        using var d = new OpenFileDialog { Filter = L.UploadPanel_007, Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in d.FileNames)
        {
            var comp = ComponentFor(path);
            var ver = ReadVersion(path);
            var item = new ListViewItem(Path.GetFileName(path)) { Tag = new Pkg(path, comp, ver), ToolTipText = path };
            item.SubItems.Add(comp);
            item.SubItems.Add(string.IsNullOrWhiteSpace(ver) ? L.UploadPanel_014 : ver);
            _list.Items.Add(item);
        }
        _status.Text = L.Format(L.UploadPanel_008, _list.Items.Count);
    }

    private void ReapplyComponent()
    {
        foreach (ListViewItem it in _list.Items)
        {
            if (it.Tag is not Pkg p) continue;
            var comp = ComponentFor(p.Path);
            it.Tag = p with { Component = comp };
            it.SubItems[1].Text = comp;
        }
    }

    private void ApplyVersion()
    {
        var v = _verInput.Text.Trim();
        if (v.Length == 0) { _status.Text = L.UploadPanel_009; return; }
        foreach (ListViewItem it in _list.SelectedItems)
        {
            if (it.Tag is not Pkg p) continue;
            it.Tag = p with { Version = v };
            it.SubItems[2].Text = v;
        }
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
        if (_list.Items.Count == 0) { _status.Text = L.UploadPanel_010; return; }
        var channel = (string)_channel.SelectedItem!;
        try
        {
            Enabled = false;
            int done = 0;
            foreach (ListViewItem it in _list.Items)
            {
                if (it.Tag is not Pkg p) continue;
                if (string.IsNullOrWhiteSpace(p.Version)) { _status.Text = L.Format(L.UploadPanel_011, Path.GetFileName(p.Path)); continue; }
                _status.Text = L.Format(L.UploadPanel_012, done + 1, _list.Items.Count, Path.GetFileName(p.Path), channel, p.Component, p.Version);
                await _api.UploadPackageAsync(channel, p.Component, p.Version, p.Path);
                done++;
            }
            _status.Text = L.Format(L.UploadPanel_013, done);
            Enabled = true;
            if (done > 0) Uploaded?.Invoke();
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_019 + ex.Message; Enabled = true; }
    }
}
