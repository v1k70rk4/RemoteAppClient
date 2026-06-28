using System;
using System.Drawing;
using System.Windows.Forms;

namespace RemoteClient;

/// <summary>
/// Reusable owner-drawn table: a rounded card with an uppercase header band and hover rows. Callers set
/// columns via <see cref="SetColumns"/>, add row items via <see cref="Add"/>, and paint each row through
/// <see cref="PaintRow"/> (using <see cref="RowPaintEventArgs.Cell"/>/<see cref="RowPaintEventArgs.Text"/>
/// to place cells). Double-click / Enter raises <see cref="RowActivated"/>. See design_handoff_console_redesign.
/// </summary>
public sealed class OwnerList : UserControl
{
    public sealed record Col(string Title, int Width, bool Right = false);

    private readonly ListView _lv = new();
    private readonly Panel _card;
    private Col[] _cols = Array.Empty<Col>();
    private int _hover = -1;
    private int _sortCol = -1;
    private bool _sortAsc = true;

    public event EventHandler<RowPaintEventArgs>? PaintRow;
    public event Action<object>? RowActivated;
    public event Action<object, Point>? RowRightClicked;   // (item, screen point)
    public event Action<int>? HeaderClicked;               // (column index) — opt-in sorting; the caller re-orders rows + calls SetSort

    public OwnerList(int rowHeight = 46)
    {
        DoubleBuffered = true;
        BackColor = ThemeManager.Bg;

        _lv.View = View.Details;
        _lv.OwnerDraw = true;
        _lv.FullRowSelect = true;
        _lv.MultiSelect = false;
        _lv.HeaderStyle = ColumnHeaderStyle.None;
        _lv.BorderStyle = BorderStyle.None;
        _lv.BackColor = ThemeManager.Panel;
        _lv.Dock = DockStyle.Fill;
        _lv.Columns.Add("", 100);
        _lv.SmallImageList = new ImageList { ImageSize = new Size(1, rowHeight) }; // forces row height
        _lv.DrawItem += OnDrawItem;
        _lv.DrawSubItem += (_, e) => e.DrawDefault = false;
        _lv.SizeChanged += (_, _) => { if (_lv.Columns.Count > 0) _lv.Columns[0].Width = _lv.ClientSize.Width; };
        _lv.MouseMove += (_, e) => { int i = _lv.GetItemAt(e.X, e.Y)?.Index ?? -1; if (i != _hover) { _hover = i; _lv.Invalidate(); } };
        _lv.MouseLeave += (_, _) => { if (_hover != -1) { _hover = -1; _lv.Invalidate(); } };
        _lv.DoubleClick += (_, _) => { if (_lv.SelectedItems.Count > 0) RowActivated?.Invoke(_lv.SelectedItems[0].Tag!); };
        _lv.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _lv.SelectedItems.Count > 0) RowActivated?.Invoke(_lv.SelectedItems[0].Tag!); };
        _lv.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _lv.GetItemAt(e.X, e.Y);
            if (hit?.Tag is { } tag) { hit.Selected = true; RowRightClicked?.Invoke(tag, _lv.PointToScreen(e.Location)); }
        };
        typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_lv, true);

        _card = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Panel, Padding = new Padding(1, 36, 1, 6) };
        _card.Paint += PaintCardChrome;
        _card.MouseClick += OnHeaderClick;
        _card.MouseMove += (_, e) => _card.Cursor = HeaderClicked is not null && e.Y <= 34 ? Cursors.Hand : Cursors.Default;
        _card.Controls.Add(_lv);
        Controls.Add(_card);
    }

    public void SetColumns(params Col[] cols) { _cols = cols; _card.Invalidate(); }
    /// <summary>Shows the sort arrow on a column (the caller owns the actual row ordering).</summary>
    public void SetSort(int column, bool ascending) { _sortCol = column; _sortAsc = ascending; _card.Invalidate(); }

    private void OnHeaderClick(object? sender, MouseEventArgs e)
    {
        if (e.Y > 34 || _cols.Length == 0 || HeaderClicked is null) return;   // header band only, and only when sorting is wired
        var xs = ColumnX(_card.Width);
        for (int i = 0; i < _cols.Length; i++)
        {
            int right = i + 1 < _cols.Length ? xs[i + 1] : xs[_cols.Length];
            if (e.X >= xs[i] && e.X < right) { HeaderClicked.Invoke(i); return; }
        }
    }
    public object? Selected => _lv.SelectedItems.Count == 0 ? null : _lv.SelectedItems[0].Tag;
    public void BeginUpdate() => _lv.BeginUpdate();
    public void EndUpdate() => _lv.EndUpdate();
    public void Clear() => _lv.Items.Clear();
    public void Add(object item) => _lv.Items.Add(new ListViewItem { Tag = item });

    private int[] ColumnX(int width)
    {
        var xs = new int[_cols.Length + 1];
        int x = 18;
        for (int i = 0; i < _cols.Length; i++) { xs[i] = x; x += _cols[i].Width; }
        xs[_cols.Length] = width - 14;
        return xs;
    }

    private void PaintCardChrome(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        UiPaint.DrawCard(g, new Rectangle(0, 0, _card.Width - 1, _card.Height - 1), 12, ThemeManager.Panel, ThemeManager.BorderSoft);
        var xs = ColumnX(_card.Width);
        for (int i = 0; i < _cols.Length; i++)
        {
            int right = i + 1 < _cols.Length ? xs[i + 1] : xs[_cols.Length];
            var r = new Rectangle(xs[i], 0, right - xs[i] - 8, 32);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | (_cols[i].Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            string title = _cols[i].Title.ToUpperInvariant();
            if (i == _sortCol) title += _sortAsc ? "  ▴" : "  ▾";
            TextRenderer.DrawText(g, title, UiFont.Label, r, ThemeManager.Text3, flags);
        }
        using var pen = new Pen(ThemeManager.BorderSoft);
        g.DrawLine(pen, 12, 33, _card.Width - 12, 33);
    }

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        var rect = new Rectangle(0, e.Bounds.Top, _lv.ClientSize.Width, e.Bounds.Height);
        using (var bg = new SolidBrush(e.ItemIndex == _hover ? ThemeManager.Panel2 : ThemeManager.Panel)) g.FillRectangle(bg, rect);
        if (e.Item?.Tag is { } item)
            PaintRow?.Invoke(this, new RowPaintEventArgs(g, rect, e.ItemIndex, item, ColumnX(_lv.ClientSize.Width), _cols));
        using (var pen = new Pen(ThemeManager.BorderSoft)) g.DrawLine(pen, rect.Left + 10, rect.Bottom - 1, rect.Right - 10, rect.Bottom - 1);
    }
}

/// <summary>Per-row paint context handed to <see cref="OwnerList.PaintRow"/>.</summary>
public sealed class RowPaintEventArgs : EventArgs
{
    private readonly int[] _x;
    private readonly OwnerList.Col[] _cols;

    public Graphics G { get; }
    public Rectangle Bounds { get; }
    public int Index { get; }
    public object Item { get; }
    public int Cy => Bounds.Top + Bounds.Height / 2;

    public RowPaintEventArgs(Graphics g, Rectangle bounds, int index, object item, int[] x, OwnerList.Col[] cols)
    { G = g; Bounds = bounds; Index = index; Item = item; _x = x; _cols = cols; }

    /// <summary>Cell rectangle for column i (last column stretches to the right edge), inset for ellipsis room.</summary>
    public Rectangle Cell(int i)
    {
        int right = i + 1 < _cols.Length ? _x[i + 1] : _x[_cols.Length];
        return new Rectangle(_x[i], Bounds.Top, right - _x[i] - 12, Bounds.Height);
    }

    /// <summary>Draws cell text vertically centered, with the column's alignment.</summary>
    public void Text(int i, string text, Font font, Color color)
    {
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis
                    | (_cols[i].Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(G, text, font, Cell(i), color, flags);
    }
}
