using System.Diagnostics;
using System.Drawing;
using MaterialSkin.Controls;

namespace RemoteClient;

/// <summary>
/// Egy vagy több exe feltöltése egy release-csatornára. A komponenst (agent/updater/client) és a
/// verziót a fájlnévből / az exe verzió-infójából olvassa ki; tömeges (bulk) feltöltés is mehet.
/// </summary>
public sealed class UploadPackageForm : MaterialForm
{
    private readonly AdminApi _api;
    private readonly MaterialComboBox _channel = new() { Hint = "Csatorna" };
    private readonly MaterialComboBox _override = new() { Hint = "Komponens" };
    private readonly ListView _list = new();
    private readonly MaterialLabel _status = new();

    private sealed record Pkg(string Path, string Component, string Version);

    public UploadPackageForm(AdminApi api)
    {
        _api = api;
        ThemeManager.Skin.AddFormToManage(this);
        Text = "Exe-k feltöltése csatornára";
        Sizable = false;
        Width = 660; Height = 480;
        StartPosition = FormStartPosition.CenterParent;

        _channel.Width = 120; _channel.Margin = new Padding(4, 0, 12, 0);
        _channel.Items.AddRange(["rtm", "beta"]); _channel.SelectedIndex = 1;
        _override.Width = 150; _override.Margin = new Padding(4, 0, 12, 0);
        _override.Items.AddRange(["(auto)", "agent", "updater", "client"]); _override.SelectedIndex = 0;
        _override.SelectedIndexChanged += (_, _) => ReapplyComponent();

        var addBtn = new MaterialButton { Text = "Fájlok hozzáadása…", AutoSize = true, Margin = new Padding(4, 0, 4, 0) };
        addBtn.Click += (_, _) => AddFiles();
        var rmBtn = new MaterialButton { Text = "Eltávolítás", AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        rmBtn.Click += (_, _) => { foreach (ListViewItem it in _list.SelectedItems) it.Remove(); };
        var clrBtn = new MaterialButton { Text = "Ürítés", AutoSize = true, Margin = new Padding(4, 0, 4, 0), Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        clrBtn.Click += (_, _) => _list.Items.Clear();

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(16, 12, 16, 6) };
        toolbar.Controls.AddRange([_channel, _override, addBtn, rmBtn, clrBtn]);

        _list.View = View.Details; _list.FullRowSelect = true; _list.Dock = DockStyle.Fill; _list.BorderStyle = BorderStyle.None;
        _list.Columns.Add("Fájl", 300);
        _list.Columns.Add("Komponens", 120);
        _list.Columns.Add("Verzió", 180);
        ThemeManager.StyleList(_list);

        var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        _status.AutoSize = false; _status.Dock = DockStyle.Fill; _status.AutoEllipsis = true;
        _status.TextAlign = ContentAlignment.MiddleLeft; _status.Padding = new Padding(16, 0, 16, 0);
        statusPanel.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 56, Padding = new Padding(0, 8, 16, 8) };
        var ok = new MaterialButton { Text = "Feltöltés", AutoSize = true };
        ok.Click += async (_, _) => await UploadAsync();
        var cancel = new MaterialButton { Text = "Mégse", DialogResult = DialogResult.Cancel, AutoSize = true, Type = MaterialButton.MaterialButtonType.Outlined, HighEmphasis = false };
        buttons.Controls.AddRange([ok, cancel]);

        Controls.Add(_list);       // Fill
        Controls.Add(toolbar);     // Top
        Controls.Add(statusPanel); // Bottom
        Controls.Add(buttons);     // Bottom (legalsó)
        CancelButton = cancel;
    }

    private void AddFiles()
    {
        using var d = new OpenFileDialog { Filter = "Exe|*.exe", Multiselect = true };
        if (d.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in d.FileNames)
        {
            var comp = ComponentFor(path);
            var ver = ReadVersion(path);
            var item = new ListViewItem(Path.GetFileName(path)) { Tag = new Pkg(path, comp, ver), ToolTipText = path };
            item.SubItems.Add(comp);
            item.SubItems.Add(string.IsNullOrWhiteSpace(ver) ? "(ismeretlen)" : ver);
            _list.Items.Add(item);
        }
        _status.Text = $"{_list.Items.Count} fájl a listában.";
    }

    /// <summary>A kiválasztott komponens-felülírás (vagy auto-detektálás) érvényesítése minden soron.</summary>
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

    private string ComponentFor(string path)
    {
        if (_override.SelectedItem is string o && o != "(auto)") return o;
        var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
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
        if (_list.Items.Count == 0) { _status.Text = "Adj hozzá legalább egy exe-t."; return; }
        var channel = (string)_channel.SelectedItem!;
        try
        {
            Enabled = false;
            int done = 0;
            foreach (ListViewItem it in _list.Items)
            {
                if (it.Tag is not Pkg p) continue;
                if (string.IsNullOrWhiteSpace(p.Version)) { _status.Text = $"Nincs verzió: {Path.GetFileName(p.Path)} — kihagyva."; continue; }
                _status.Text = $"Feltöltés ({done + 1}/{_list.Items.Count}): {Path.GetFileName(p.Path)} → {channel}/{p.Component} {p.Version}…";
                await _api.UploadPackageAsync(channel, p.Component, p.Version, p.Path);
                done++;
            }
            _status.Text = $"{done} csomag feltöltve.";
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { _status.Text = "Hiba: " + ex.Message; Enabled = true; }
    }
}
