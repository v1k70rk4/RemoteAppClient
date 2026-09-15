using System.Drawing;
using System.Globalization;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;
using Outcome = RemoteClient.Views.NoteImport.Outcome;

namespace RemoteClient.Views;

/// <summary>
/// Bulk note import: open or paste a "hostname;note" list, see line by line what it would do, then save it in
/// one call. Nothing is written before Save, and then only the lines marked New - plus Overwrite while that
/// switch is on. The matching rules live in <see cref="NoteImport"/>.
/// </summary>
public sealed class NoteImportWindow : MaterialForm
{
    private readonly AdminApi _api;
    private readonly IReadOnlyList<DeviceInfo> _devices;
    private readonly OwnerList _list = new();
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly UiButton _open = new(L.NoteImport_OpenFile, UiButton.Style.Outline, "folder");
    private readonly UiButton _paste = new(L.NoteImport_Paste, UiButton.Style.Outline);
    // Off by default: a note someone typed by hand is kept unless the operator decides the list should win.
    private readonly UiToggle _overwrite = new(L.NoteImport_Overwrite);
    private readonly UiButton _copyNotFound = new(L.NoteImport_CopyNotFound, UiButton.Style.Outline);
    private readonly UiButton _save = new(L.Format(L.NoteImport_Save, 8888));   // sized for the widest count; the label is centred
    private List<NoteImport.Row> _rows = [];
    private string? _source;   // file name + detected encoding; null for pasted text
    private int _sortCol = -1;
    private bool _sortAsc = true;

    /// <summary>Notes the save actually changed; the device list only needs a refresh when this is above zero.</summary>
    public int Updated { get; private set; }

    public NoteImportWindow(AdminApi api, IReadOnlyList<DeviceInfo> devices)
    {
        _api = api; _devices = devices;
        // Not AddFormToManage: it would re-theme the main window's inputs (see SessionInfoWindow).
        Text = L.NoteImport_Title;
        Sizable = true;
        Size = new Size(1100, 720);
        MinimumSize = new Size(780, 480);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = ThemeManager.Bg;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.V) { PasteClipboard(); e.Handled = true; } };

        _open.Margin = new Padding(0, 0, 8, 0);
        _paste.Margin = new Padding(0, 0, 8, 0);
        _open.Click += (_, _) => OpenFile();
        _paste.Click += (_, _) => PasteClipboard();
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, WrapContents = false, BackColor = ThemeManager.Bg };
        toolbar.Controls.AddRange([_open, _paste]);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 44, Text = L.NoteImport_Hint, Font = UiFont.Body,
            ForeColor = ThemeManager.Text2, BackColor = ThemeManager.Bg, TextAlign = ContentAlignment.MiddleLeft,
        };
        _summary.Font = UiFont.BodySemi;
        _summary.ForeColor = ThemeManager.Text;
        _summary.BackColor = ThemeManager.Bg;

        _list.Dock = DockStyle.Fill;
        _list.SetColumns(
            new OwnerList.Col(L.NoteImport_ColLine, 64),
            new OwnerList.Col(L.DevicesView_Device, 240),
            new OwnerList.Col(L.NoteImport_ColResult, 160),
            new OwnerList.Col(L.NoteImport_ColCurrent, 270),
            new OwnerList.Col(L.NoteImport_ColNew, 200));
        _list.PaintRow += PaintRow;
        _list.HeaderClicked += SortBy;

        _overwrite.Dock = DockStyle.Left;
        _overwrite.CheckedChanged += (_, _) => { UpdateSummary(); _list.Invalidate(true); };
        _status.Font = UiFont.Body;
        _status.ForeColor = ThemeManager.Text2;
        _status.BackColor = ThemeManager.Bg;
        _status.Padding = new Padding(16, 0, 0, 0);

        var cancel = new UiButton(L.ConsentWaitForm_Cancel, UiButton.Style.Outline);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        CancelButton = cancel;   // Esc closes; deliberately no AcceptButton - Enter must not save hundreds of notes
        _copyNotFound.Click += (_, _) => CopyNotFound();
        _save.Click += async (_, _) => await SaveAsync();
        foreach (var b in new Control[] { _copyNotFound, cancel, _save }) b.Margin = new Padding(8, 0, 0, 0);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            BackColor = ThemeManager.Bg, Padding = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.AddRange([_copyNotFound, cancel, _save]);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = ThemeManager.Bg };
        bottom.Controls.Add(_status);     // Fill: between the switch and the buttons
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(_overwrite);

        // Fill first, then the edges: WinForms docks the last-added control outermost.
        var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 8, 16, 8), BackColor = ThemeManager.Bg };
        root.Controls.Add(_list);
        root.Controls.Add(bottom);
        root.Controls.Add(_summary);
        root.Controls.Add(hint);
        root.Controls.Add(toolbar);
        Controls.Add(root);

        UpdateSummary();
    }

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog { Filter = L.NoteImport_FileFilter, Title = L.NoteImport_Title };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            // Share read AND write: the list is often still open in Excel, which holds the file for writing.
            using var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            var (text, encoding) = NoteImport.Decode(ms.ToArray());
            ShowPreview(text, $"{Path.GetFileName(dlg.FileName)} · {L.Format(L.NoteImport_Encoding, encoding)}");
        }
        catch (Exception ex) { _status.Text = L.NoteImport_ReadError + ex.Message; }
    }

    private void PasteClipboard()
    {
        if (!_paste.Enabled) return;   // busy saving
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception ex) { _status.Text = ex.Message; return; }   // another process holds the clipboard open
        if (string.IsNullOrWhiteSpace(text)) { _status.Text = L.NoteImport_ClipboardEmpty; return; }
        ShowPreview(text, null);
    }

    private void ShowPreview(string text, string? source)
    {
        _rows = NoteImport.Build(text, _devices);
        _source = source;
        _sortCol = -1; _sortAsc = true;   // file order first, so the line numbers read like the file itself
        _status.Text = "";
        RenderRows();
        UpdateSummary();
    }

    private void SortBy(int col)
    {
        if (_rows.Count == 0) return;
        _sortAsc = col != _sortCol || !_sortAsc;
        _sortCol = col;
        RenderRows();
    }

    private void RenderRows()
    {
        IEnumerable<NoteImport.Row> rows = _rows;
        if (_sortCol >= 0)
        {
            IOrderedEnumerable<NoteImport.Row> Order<TKey>(Func<NoteImport.Row, TKey> key, IComparer<TKey>? cmp = null) =>
                _sortAsc ? _rows.OrderBy(key, cmp) : _rows.OrderByDescending(key, cmp);
            var text = StringComparer.CurrentCultureIgnoreCase;
            // Ties keep file order, so sorting by result still lists each group top to bottom.
            rows = (_sortCol switch
            {
                1 => Order(r => r.Host, text),
                2 => Order(r => r.Outcome),
                3 => Order(r => r.Device?.Note ?? "", text),
                4 => Order(r => r.Note, text),
                _ => Order(r => r.Line),
            }).ThenBy(r => r.Line);
        }
        _list.BeginUpdate();
        _list.Clear();
        foreach (var r in rows) _list.Add(r);
        _list.EndUpdate();
        _list.SetSort(_sortCol, _sortAsc);
    }

    private void UpdateSummary()
    {
        int Count(Outcome o) => _rows.Count(r => r.Outcome == o);
        var parts = Enum.GetValues<Outcome>()
            .Where(o => o != Outcome.Header && Count(o) > 0)
            .Select(o => $"{Chip(o).Text}: {Count(o)}")
            .ToList();
        if (_source is not null) parts.Add(_source);
        _summary.Text = _rows.Count == 0 ? L.NoteImport_Empty : string.Join("   ·   ", parts);

        int toSave = NoteImport.Request(_rows, _overwrite.Checked).Items.Count;
        _save.Text = L.Format(L.NoteImport_Save, toSave);
        _save.Enabled = toSave > 0 && _open.Enabled;
        _copyNotFound.Enabled = Count(Outcome.NotFound) > 0;
    }

    private void PaintRow(object? sender, RowPaintEventArgs e)
    {
        var r = (NoteImport.Row)e.Item;
        e.Text(0, r.Line.ToString(CultureInfo.CurrentCulture), UiFont.MonoSmall, ThemeManager.Text3);

        // Device cell: the name as written, with a second line when there is something to explain about it.
        if (Detail(r) is { } detail)
        {
            var cell = e.Cell(1);
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            int half = cell.Height / 2;
            TextRenderer.DrawText(e.G, r.Host, UiFont.Mono, new Rectangle(cell.X, cell.Y, cell.Width, half), ThemeManager.Text, flags | TextFormatFlags.Bottom);
            TextRenderer.DrawText(e.G, detail, UiFont.Small, new Rectangle(cell.X, cell.Y + half + 2, cell.Width, half - 2), ThemeManager.Text3, flags);
        }
        else e.Text(1, r.Host, UiFont.Mono, ThemeManager.Text);

        var (chip, fg, bg) = Chip(r.Outcome);
        UiPaint.DrawPill(e.G, e.Cell(2).X, e.Cy, chip, fg, bg, UiFont.Label, false);

        e.Text(3, OneLine(r.Device?.Note), UiFont.Body, ThemeManager.Text2);
        bool writes = r.Outcome == Outcome.New || (r.Outcome == Outcome.Overwrite && _overwrite.Checked);
        e.Text(4, OneLine(r.Note), UiFont.Body, writes ? ThemeManager.Text : ThemeManager.Text3);
    }

    private (string Text, Color Fg, Color Bg) Chip(Outcome o) => o switch
    {
        Outcome.New => (L.NoteImport_StatusNew, ThemeManager.OkFg, ThemeManager.OkBg),
        Outcome.Overwrite when _overwrite.Checked => (L.NoteImport_StatusOverwrite, ThemeManager.Accent, ThemeManager.AccentSoft),
        Outcome.Overwrite => (L.NoteImport_StatusKeep, ThemeManager.OffFg, ThemeManager.OffBg),
        Outcome.NotFound => (L.NoteImport_StatusNotFound, ThemeManager.WarnFg, ThemeManager.WarnBg),
        Outcome.Invalid => (L.NoteImport_StatusInvalid, ThemeManager.DangerFg, ThemeManager.DangerBg),
        Outcome.Repeated => (L.NoteImport_StatusRepeated, ThemeManager.OffFg, ThemeManager.OffBg),
        Outcome.EmptyNote => (L.NoteImport_StatusEmptyNote, ThemeManager.OffFg, ThemeManager.OffBg),
        Outcome.Unchanged => (L.NoteImport_StatusUnchanged, ThemeManager.OffFg, ThemeManager.OffBg),
        _ => (L.NoteImport_StatusHeader, ThemeManager.OffFg, ThemeManager.OffBg),
    };

    private static string? Detail(NoteImport.Row r) => r.Outcome switch
    {
        Outcome.Invalid => L.NoteImport_DetailInvalid,
        Outcome.Repeated => L.Format(L.NoteImport_DetailRepeated, r.SupersededBy),
        _ when r.SameName > 1 => L.Format(L.NoteImport_DetailDuplicates, r.SameName),
        _ when r.MatchedAs is not null => L.Format(L.NoteImport_DetailMatchedAs, r.MatchedAs),
        _ => null,
    };

    private static string OneLine(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim().ReplaceLineEndings(" · ");

    private void CopyNotFound()
    {
        var names = _rows.Where(r => r.Outcome == Outcome.NotFound)
            .Select(r => r.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, names));
            _status.Text = L.Format(L.NoteImport_CopiedNotFound, names.Count);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }

    private async Task SaveAsync()
    {
        var request = NoteImport.Request(_rows, _overwrite.Checked);
        if (request.Items.Count == 0) return;
        int overwrites = request.Items.Count - _rows.Count(r => r.Outcome == Outcome.New);
        if (MessageBox.Show(this, L.Format(L.NoteImport_Confirm, request.Items.Count, overwrites), L.NoteImport_Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetBusy(true);
        _status.Text = L.NoteImport_Saving;
        try
        {
            var result = await _api.ImportDeviceNotesAsync(request);
            if (result is null)
            {
                _status.Text = L.NoteImport_ServerTooOld;
                MessageBox.Show(this, L.NoteImport_ServerTooOld, L.NoteImport_Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Updated = result.Updated;
            var done = L.Format(L.NoteImport_Done, result.Updated);
            // Only possible when someone edited a note or deleted a device between the preview and the save.
            if (result.Unchanged + result.NotFound > 0) done += "\n\n" + L.Format(L.NoteImport_DoneRace, result.Unchanged, result.NotFound);
            MessageBox.Show(this, done, L.NoteImport_Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { _status.Text = L.NoteImport_Error + ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _open.Enabled = _paste.Enabled = _overwrite.Enabled = !busy;
        UseWaitCursor = busy;
        UpdateSummary();   // re-derives Save's enabled state from the rows, now that _open says whether we are busy
    }
}
