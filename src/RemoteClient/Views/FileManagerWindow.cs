using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Filesystem behind a pane: local (System.IO) or remote (the device file service over HTTP).</summary>
internal interface IFsBackend
{
    Task<FsList> ListAsync(string path, CancellationToken ct);   // "" => drive roots
    Task<FsList> HomeAsync(CancellationToken ct);
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);
    Task WriteAsync(string path, Stream content, CancellationToken ct);
    Task MkdirAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
}

internal sealed class LocalBackend : IFsBackend
{
    public Task<FsList> ListAsync(string path, CancellationToken ct)
    {
        var list = new FsList { Path = path };
        if (string.IsNullOrEmpty(path))
        {
            foreach (var d in DriveInfo.GetDrives()) if (d.IsReady) list.Entries.Add(new FsEntry { Name = d.Name, IsDir = true });
            return Task.FromResult(list);
        }
        var di = new DirectoryInfo(path);
        // Skip OS-protected entries (Hidden+System): pagefile.sys and the legacy compat junctions
        // (e.g. "Documents"/"Dokumentumok" -> "My Documents") that Windows denies enumerating anyway.
        const FileAttributes protectedOs = FileAttributes.Hidden | FileAttributes.System;
        foreach (var dir in di.GetDirectories()) { try { if ((dir.Attributes & protectedOs) == protectedOs) continue; list.Entries.Add(new FsEntry { Name = dir.Name, IsDir = true, Modified = dir.LastWriteTimeUtc }); } catch { } }
        foreach (var f in di.GetFiles()) { try { if ((f.Attributes & protectedOs) == protectedOs) continue; list.Entries.Add(new FsEntry { Name = f.Name, Size = f.Length, Modified = f.LastWriteTimeUtc }); } catch { } }
        return Task.FromResult(list);
    }
    public Task<FsList> HomeAsync(CancellationToken ct) => ListAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ct);
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(path));
    public async Task WriteAsync(string path, Stream content, CancellationToken ct) { await using var fs = File.Create(path); await content.CopyToAsync(fs, ct); }
    public Task MkdirAsync(string path, CancellationToken ct) { Directory.CreateDirectory(path); return Task.CompletedTask; }
    public Task DeleteAsync(string path, CancellationToken ct) { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); return Task.CompletedTask; }
    public Task RenameAsync(string from, string to, CancellationToken ct) { if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to); return Task.CompletedTask; }
}

/// <summary>
/// A listed name joined to a folder - only when it is a plain name (<see cref="SafeNames.IsPlainName"/>) and the
/// result stays inside <c>dir</c>, as a second check. Null otherwise.
/// </summary>
internal static class SafeChild
{
    public static string? Of(string dir, string name)
    {
        if (!SafeNames.IsPlainName(name)) return null;
        try
        {
            var path = System.IO.Path.Combine(dir, name);
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir)) + System.IO.Path.DirectorySeparatorChar;
            return System.IO.Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        catch { return null; }
    }
}

internal sealed class RemoteBackend(FileClient fc) : IFsBackend
{
    public async Task<FsList> ListAsync(string path, CancellationToken ct) => (string.IsNullOrEmpty(path) ? await fc.DrivesAsync(ct) : await fc.ListAsync(path, ct)) ?? new FsList { Path = path };
    public async Task<FsList> HomeAsync(CancellationToken ct) => await fc.HomeAsync(ct) ?? new FsList();
    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => fc.OpenReadAsync(path, ct);
    public Task WriteAsync(string path, Stream content, CancellationToken ct) => fc.UploadAsync(path, content, ct);
    public Task MkdirAsync(string path, CancellationToken ct) => fc.MkdirAsync(path, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => fc.DeleteAsync(path, ct);
    public Task RenameAsync(string from, string to, CancellationToken ct) => fc.RenameAsync(from, to, ct);
}

/// <summary>One pane: a drive selector, a path bar, and a file list backed by an <see cref="IFsBackend"/>.</summary>
internal sealed class FilePane : UserControl
{
    private readonly IFsBackend _backend;
    private readonly ComboBox _drives = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Panel, ForeColor = ThemeManager.Text };
    private readonly Label _pathLbl = new() { Dock = DockStyle.Top, AutoEllipsis = true, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), BackColor = ThemeManager.Bg, ForeColor = ThemeManager.Text2 };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
    private readonly CancellationToken _ct;
    private bool _suppressDrive;
    // Sort: a click on a header picks the column, a second click flips the direction. Folders always stay
    // above files and ".." on top; the sort orders within those groups and survives navigation.
    private int _sortCol;
    private bool _sortAsc = true;
    private FsList? _last;

    public string Path { get; private set; } = "";
    public IFsBackend Backend => _backend;
    public event Action? Activated2;

    public FilePane(IFsBackend backend, string title, CancellationToken ct)
    {
        _backend = backend; _ct = ct; Dock = DockStyle.Fill; BackColor = ThemeManager.Bg;
        _list.Columns.Add(title, 240);
        _list.Columns.Add(L.FileManager_Size, 90, HorizontalAlignment.Right);
        _list.Columns.Add(L.FileManager_Modified, 130);
        _list.BackColor = ThemeManager.Panel; _list.ForeColor = ThemeManager.Text;
        _list.OwnerDraw = true;
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += DrawRow;
        _list.DrawSubItem += (_, se) => se.DrawDefault = false;
        _list.DoubleClick += async (_, _) => await OpenSelectedAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; await OpenSelectedAsync(); }
            else if (e.KeyCode == Keys.Back) { e.Handled = true; await UpAsync(); }
        };
        _list.Enter += (_, _) => Activated2?.Invoke();
        _list.Resize += (_, _) => FitColumns();
        _list.ColumnClick += (_, e) =>
        {
            // A new column starts in its natural order: names A to Z, sizes and dates largest / newest first.
            if (e.Column == _sortCol) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = e.Column == 0; }
            Render();
        };
        _drives.SelectedIndexChanged += async (_, _) => { if (!_suppressDrive && _drives.SelectedItem is string dr) await NavigateAsync(dr); };

        // Dock order: list fills; path + drives stack on top (last added is topmost).
        Controls.Add(_list);
        Controls.Add(_pathLbl);
        Controls.Add(_drives);
    }

    public async Task InitAsync()
    {
        try
        {
            var drives = await _backend.ListAsync("", _ct);
            _drives.Items.Clear();
            foreach (var d in drives.Entries) _drives.Items.Add(d.Name);
            await HomeAsync();
        }
        catch (Exception ex) { _pathLbl.Text = "⚠ " + ex.Message; }
    }

    public Task HomeAsync() => ShowAsync(() => _backend.HomeAsync(_ct));
    public Task RefreshAsync() => NavigateAsync(Path);
    public Task NavigateAsync(string path) => ShowAsync(() => _backend.ListAsync(path, _ct));

    /// <summary>
    /// Gets a new session when the old one is gone (set by the window for the device pane); true when the next
    /// try can go ahead.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Exception, Task<bool>>? Recover { get; set; }

    /// <summary>Lists and shows a folder; a lost session is recovered once and the listing tried again.</summary>
    private async Task ShowAsync(Func<Task<FsList>> list)
    {
        try { SetList(await list()); }
        catch (Exception ex)
        {
            try
            {
                if (Recover is not null && await Recover(ex)) { SetList(await list()); return; }
                _pathLbl.Text = "⚠ " + ex.Message;
            }
            catch (Exception again) { _pathLbl.Text = "⚠ " + again.Message; }
        }
    }

    private void SetList(FsList l)
    {
        Path = l.Path;
        _pathLbl.Text = string.IsNullOrEmpty(l.Path) ? L.FileManager_Drives : l.Path;
        // Reflect the current drive in the combo without re-triggering navigation.
        var root = string.IsNullOrEmpty(l.Path) ? null : System.IO.Path.GetPathRoot(l.Path);
        if (root is not null && _drives.Items.Contains(root) && (_drives.SelectedItem as string) != root)
        { _suppressDrive = true; _drives.SelectedItem = root; _suppressDrive = false; }
        _last = l;
        Render();
    }

    /// <summary>Fills the list from the folder on show in the current sort order.</summary>
    private void Render()
    {
        if (_last is not { } l) return;
        // Re-sorting must not lose what the operator has selected: the names survive the rebuild.
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ListViewItem it in _list.SelectedItems) if (it.Tag is FsEntry se) selected.Add(se.Name);
        _list.BeginUpdate();
        _list.Items.Clear();
        if (!string.IsNullOrEmpty(l.Path)) _list.Items.Add(new ListViewItem("..") { Tag = null });
        foreach (var e in Sorted(l.Entries.Where(e => e.IsDir)))
            _list.Items.Add(new ListViewItem(new[] { "📁 " + e.Name, "", e.Modified == default ? "" : e.Modified.LocalDateTime.ToString("g") }) { Tag = e });
        foreach (var e in Sorted(l.Entries.Where(e => !e.IsDir)))
            _list.Items.Add(new ListViewItem(new[] { e.Name, FmtSize(e.Size), e.Modified == default ? "" : e.Modified.LocalDateTime.ToString("g") }) { Tag = e });
        if (selected.Count > 0)
            foreach (ListViewItem it in _list.Items) if (it.Tag is FsEntry fe && selected.Contains(fe.Name)) it.Selected = true;
        _list.EndUpdate();
        FitColumns();
        _list.Invalidate(); // the header's sort mark
    }

    /// <summary>The entries in the chosen column's order; name decides ties, so the order is stable.</summary>
    private IEnumerable<FsEntry> Sorted(IEnumerable<FsEntry> entries)
    {
        var byName = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
        IOrderedEnumerable<FsEntry> ordered = _sortCol switch
        {
            1 => _sortAsc ? entries.OrderBy(e => e.Size) : entries.OrderByDescending(e => e.Size),
            2 => _sortAsc ? entries.OrderBy(e => e.Modified) : entries.OrderByDescending(e => e.Modified),
            _ => _sortAsc ? byName : entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };
        return _sortCol == 0 ? ordered : ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
    }

    private async Task OpenSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0) return;
        var item = _list.SelectedItems[0];
        if (item.Text == "..") { await UpAsync(); return; }
        if (item.Tag is FsEntry { IsDir: true } e) await NavigateAsync(System.IO.Path.Combine(Path, e.Name));
    }

    private async Task UpAsync()
    {
        if (string.IsNullOrEmpty(Path)) return;
        var parent = System.IO.Path.GetDirectoryName(Path?.TrimEnd('\\'));
        await NavigateAsync(parent ?? ""); // null at a drive root -> drive list
    }

    /// <summary>Whether the folder on show has an entry of this name (the copy asks before overwriting it).</summary>
    public bool Contains(string name)
    {
        foreach (ListViewItem it in _list.Items)
            if (it.Tag is FsEntry e && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Selected entries as (entry, full path), skipping the ".." row.</summary>
    public List<(FsEntry Entry, string Path)> SelectedEntries()
    {
        var res = new List<(FsEntry, string)>();
        foreach (ListViewItem it in _list.SelectedItems)
            if (it.Tag is FsEntry e) res.Add((e, System.IO.Path.Combine(Path, e.Name)));
        return res;
    }

    private static string FmtSize(long b) => b >= 1 << 30 ? $"{b / (1 << 30)} GB" : b >= 1 << 20 ? $"{b / (1 << 20)} MB" : b >= 1 << 10 ? $"{b / (1 << 10)} KB" : $"{b} B";

    /// <summary>Name column fills the pane (≈double the default); size and modified auto-fit their content.</summary>
    private void FitColumns()
    {
        if (_list.Columns.Count < 3) return;
        // Fixed size/modified + name fills the rest using the control width (not ClientSize) with a
        // scrollbar reserve, so both panes (equal width via the 50-50 split) get an identical name column.
        const int sizeW = 90, modW = 150;
        _list.Columns[1].Width = sizeW;
        _list.Columns[2].Width = modW;
        var rest = _list.Width - sizeW - modW - SystemInformation.VerticalScrollBarWidth - 4;
        _list.Columns[0].Width = Math.Max(200, rest);
    }

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var bg = new SolidBrush(ThemeManager.Panel2)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", UiFont.Label, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
            ThemeManager.Text3, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (e.ColumnIndex == _sortCol)
            TextRenderer.DrawText(e.Graphics, _sortAsc ? "▴" : "▾", UiFont.Body, new Rectangle(e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height),
                ThemeManager.Text2, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        using var pen = new Pen(ThemeManager.BorderSoft);
        e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
    }

    private void DrawRow(object? sender, DrawListViewItemEventArgs e)
    {
        var g = e.Graphics;
        using (var bg = new SolidBrush(e.Item.Selected ? ThemeManager.Panel2 : ThemeManager.Panel)) g.FillRectangle(bg, e.Bounds);
        int x = e.Bounds.Left;
        for (int i = 0; i < _list.Columns.Count && i < e.Item.SubItems.Count; i++)
        {
            int cw = _list.Columns[i].Width;
            var flags = (i == 1 ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(g, e.Item.SubItems[i].Text, i == 0 ? UiFont.Body : UiFont.MonoSmall,
                new Rectangle(x + 8, e.Bounds.Top, cw - 12, e.Bounds.Height), i == 0 ? ThemeManager.Text : ThemeManager.Text2, flags);
            x += cw;
        }
    }
}

/// <summary>Total Commander-style two-pane file manager: local PC (left) ↔ remote device (right).</summary>
public sealed class FileManagerWindow : MaterialForm
{
    private readonly FilePane _left, _right;
    private FilePane _active;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), BackColor = ThemeManager.Bg, ForeColor = ThemeManager.Text2 };
    private readonly MaterialProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 10, Visible = false };
    private readonly Label _progressLbl = new() { Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly FileClient _fc;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _copyCts; // per-copy, cancellable from the Mégse button / Esc
    private readonly Func<CancellationToken, Task<FileSession?>>? _reconnect;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private int _sessionGeneration;   // bumped on every new session: a caller that waited sees it was done for it

    // A lost session is reopened up to this many times per operation, waiting this long before each try: the
    // device comes back on the server a little after its link does, so the first try alone is often too early.
    private static readonly TimeSpan[] ReconnectDelays = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    // Copy progress: the stream only records the bytes; a timer shows them, with the speed, four times a second.
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 250 };
    private readonly System.Diagnostics.Stopwatch _fileClock = new();
    private readonly TransferMeter _meter = new();
    private long _fileCopied, _fileTotal;

    /// <param name="reconnect">
    /// Opens a new file session to the same device (new tunnel, forward and token); null when there is no way to.
    /// </param>
    public FileManagerWindow(FileSession session, string hostname, Func<CancellationToken, Task<FileSession?>>? reconnect = null)
    {
        _fc = new FileClient(session.LocalPort, session.Token);
        _reconnect = reconnect;
        // Deliberately NOT AddFormToManage(this): doing so makes MaterialSkin re-theme EVERY managed form
        // (incl. the main window), resetting owner-drawn text inputs to its light input color. The dark
        // title bar still renders from the shared scheme; this window's body is styled manually.
        Text = $"{L.FileManager_Title} — {hostname}";
        Size = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        BackColor = ThemeManager.Bg;

        _left = new FilePane(new LocalBackend(), L.FileManager_LocalPC, _cts.Token);
        _right = new FilePane(new RemoteBackend(_fc), hostname, _cts.Token);
        _active = _left;
        _left.Activated2 += () => _active = _left;
        _right.Activated2 += () => _active = _right;
        _right.Recover = ex => RecoverOnceAsync(ex, _cts.Token);
        _progressTimer.Tick += (_, _) => RenderProgress();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_left);
        split.Panel2.Controls.Add(_right);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(8, 8, 8, 8), BackColor = ThemeManager.Bg };
        void Btn(string t, Func<Task> a) { var b = new UiButton(t, UiButton.Style.Outline) { Margin = new Padding(4, 0, 4, 0) }; b.Click += async (_, _) => await Guard(a); bar.Controls.Add(b); }
        Btn($"{L.FileManager_Copy} (F5)", CopyAsync);
        Btn($"{L.FileManager_NewFolder} (F7)", MkdirAsync);
        Btn($"{L.FileManager_Delete} (F8)", DeleteAsync);
        Btn($"{L.FileManager_Rename} (F2)", RenameAsync);
        Btn(L.AboutView_Refresh, () => _active.RefreshAsync());
        var cancelBtn = new UiButton($"{L.FileManager_Cancel} (Esc)", UiButton.Style.Outline) { Margin = new Padding(4, 0, 4, 0) };
        cancelBtn.Click += (_, _) => _copyCts?.Cancel();
        bar.Controls.Add(cancelBtn);

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(_status);
        Controls.Add(_progress);
        Controls.Add(_progressLbl);

        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { _copyCts?.Cancel(); e.Handled = true; return; }
            Func<Task>? a = e.KeyCode switch
            {
                Keys.F5 => CopyAsync,
                Keys.F7 => MkdirAsync,
                Keys.F8 or Keys.Delete => DeleteAsync,
                Keys.F2 => RenameAsync,
                _ => null,
            };
            if (a is not null) { e.Handled = true; await Guard(a); }
        };
        Shown += async (_, _) => { try { split.SplitterDistance = (split.Width - split.SplitterWidth) / 2; } catch { /* min-size constraints */ } await _left.InitAsync(); await _right.InitAsync(); };
        FormClosed += (_, _) => { _cts.Cancel(); _progressTimer.Dispose(); _fc.Dispose(); };
    }

    private FilePane Other => _active == _left ? _right : _left;
    private void SetStatus(string s) { if (InvokeRequired) BeginInvoke(() => _status.Text = s); else _status.Text = s; }

    private async Task Guard(Func<Task> a)
    {
        try { await a(); }
        catch (Exception ex)
        {
            // A lost session is reopened here too (delete, rename, new folder); the operator then repeats the step,
            // because whether it was done before the link went is not known.
            try
            {
                SetStatus(await RecoverOnceAsync(ex, _cts.Token) ? L.FileManager_ReconnectedTryAgain : L.ForgotPasswordForm_Error + ex.Message);
            }
            catch (Exception again) { SetStatus(L.ForgotPasswordForm_Error + again.Message); }
        }
    }

    /// <summary>One reconnect for a lost session (not for any other error); true when there is a new one.</summary>
    private async Task<bool> RecoverOnceAsync(Exception ex, CancellationToken ct) =>
        _reconnect is not null && FileClient.IsConnectionLost(ex) && await ReconnectAsync(Volatile.Read(ref _sessionGeneration), 1, 1, ct);

    /// <summary>
    /// Opens a new session after the old one was lost, waiting first (see <see cref="ReconnectDelays"/>). One at a
    /// time: a second caller that lost the same session waits and takes the new one instead of opening another.
    /// </summary>
    private async Task<bool> ReconnectAsync(int seenGeneration, int attempt, int attempts, CancellationToken ct)
    {
        if (_reconnect is null) return false;
        await _reconnectGate.WaitAsync(ct);
        try
        {
            if (_sessionGeneration != seenGeneration) return true;   // someone else already reconnected
            SetStatus(L.Format(L.FileManager_Reconnecting, attempt, attempts));
            await Task.Delay(ReconnectDelays[Math.Min(attempt, ReconnectDelays.Length) - 1], ct);
            FileSession? session;
            try { session = await _reconnect(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return false; }
            if (session is null) return false;
            _fc.Retarget(session);
            Interlocked.Increment(ref _sessionGeneration);
            return true;
        }
        finally { _reconnectGate.Release(); }
    }

    /// <summary>
    /// Runs a step against the device; when the session is lost it is reopened (several tries, see
    /// <see cref="ReconnectDelays"/>) and the step run again from the start. Other errors go to the caller.
    /// </summary>
    private async Task<T> WithReconnectAsync<T>(Func<Task<T>> step, CancellationToken ct)
    {
        int attempt = 0;
        while (true)
        {
            int generation = Volatile.Read(ref _sessionGeneration);
            try { return await step(); }
            catch (Exception ex) when (_reconnect is not null && !ct.IsCancellationRequested && FileClient.IsConnectionLost(ex))
            {
                while (true)
                {
                    if (++attempt > ReconnectDelays.Length) throw;
                    if (await ReconnectAsync(generation, attempt, ReconnectDelays.Length, ct)) break;
                }
                if (_copyStatus is { } s) SetStatus(s);
            }
        }
    }

    private Task WithReconnectAsync(Func<Task> step, CancellationToken ct) =>
        WithReconnectAsync(async () => { await step(); return true; }, ct);

    private string? _copyStatus;   // what the status line said before a reconnect overwrote it

    private void SetCopyStatus(string s) { _copyStatus = s; SetStatus(s); }

    private void ShowProgress(bool on)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowProgress(on)); return; }
        _progress.Visible = on; _progressLbl.Visible = on;
        if (on) _progressTimer.Start();
        else { _progressTimer.Stop(); _progress.Value = 0; _progressLbl.Text = ""; }
    }

    /// <summary>A new file: its size, and the clock and the speed from zero.</summary>
    private void StartFileProgress(long total)
    {
        Interlocked.Exchange(ref _fileCopied, 0);
        Interlocked.Exchange(ref _fileTotal, total);
        if (InvokeRequired) BeginInvoke(ResetMeter); else ResetMeter();
    }

    private void ResetMeter() { _meter.Reset(); _fileClock.Restart(); RenderProgress(); }

    /// <summary>The bar and "copied / size · speed · time left", from the timer (UI thread).</summary>
    private void RenderProgress()
    {
        long copied = Interlocked.Read(ref _fileCopied), total = Interlocked.Read(ref _fileTotal);
        _progress.Value = total > 0 ? (int)Math.Min(100, copied * 100 / total) : 0;
        var rate = _meter.Sample(_fileClock.Elapsed, copied);
        string done = TransferMeter.Size(copied), size = TransferMeter.Size(Math.Max(total, copied));
        _progressLbl.Text = rate is not { } r ? L.Format(L.FileManager_Progress, done, size)
            : TransferMeter.Remaining(copied, total, r) is { } left
                ? L.Format(L.FileManager_ProgressRate, done, size, TransferMeter.Size(r), TransferMeter.Duration(left))
                : L.Format(L.FileManager_ProgressRateNoEta, done, size, TransferMeter.Size(r));
    }

    private async Task CopyAsync()
    {
        var items = _active.SelectedEntries();
        if (items.Count == 0) { SetStatus(L.FileManager_NothingSelected); return; }
        if (string.IsNullOrEmpty(Other.Path)) { SetStatus(L.FileManager_PickDestDir); return; }
        var src = _active.Backend; var dst = Other.Backend; var dstDir = Other.Path;

        // Overwriting is asked about once, for what is selected; inside a copied folder the copy stays within it.
        int existing = items.Count(i => Other.Contains(i.Entry.Name));
        if (existing > 0 && MessageBox.Show(L.Format(L.FileManager_ConfirmOverwrite, existing), L.FileManager_Copy,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        _copyCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _copyCts.Token;
        ShowProgress(true);
        _skipped = 0; _failed = 0; _lastError = null;
        try
        {
            int n = 0;
            foreach (var (entry, path) in items)
            {
                n++;
                SetCopyStatus(L.Format(L.FileManager_Copying, entry.Name, n, items.Count));
                if (SafeChild.Of(dstDir, entry.Name) is not { } target) { _skipped++; continue; }
                await CopyEntryAsync(src, path, dst, target, entry, 0, ct);
            }
            var done = _skipped == 0 ? L.FileManager_Done : L.Format(L.FileManager_DoneSkipped, _skipped);
            SetStatus(_failed == 0 ? done : done + " " + L.Format(L.FileManager_DoneFailed, _failed, _lastError ?? ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { SetStatus(L.FileManager_Cancelled); }
        catch (Exception ex) when (FileClient.IsConnectionLost(ex)) { SetStatus(L.Format(L.FileManager_ConnectionLost, ex.Message)); }
        finally { ShowProgress(false); _copyCts.Dispose(); _copyCts = null; _copyStatus = null; }
        await Other.RefreshAsync();
    }

    private int _skipped, _failed;
    private string? _lastError;
    private const int MaxCopyDepth = 64;

    // Names come from the other side's listing - for a download, from the managed device. Each one is used only
    // when it is a single plain name that stays inside the folder it is copied into (see SafeNames); anything else
    // is skipped and counted, never followed, so a device cannot steer a write elsewhere on this machine.
    //
    // A lost session (the tunnel dropped, the device cleared its token) is reopened and the step - a folder's
    // listing, a whole file - run again from its start. Any other error belongs to that one item: it is counted,
    // a half-written download removed, and the copy goes on with the rest. Only a session that cannot be got
    // back ends the copy.
    private async Task CopyEntryAsync(IFsBackend src, string srcPath, IFsBackend dst, string dstPath, FsEntry entry, int depth, CancellationToken ct)
    {
        if (entry.IsDir)
        {
            if (depth >= MaxCopyDepth) throw new InvalidOperationException(L.Format(L.FileManager_TooDeep, MaxCopyDepth));
            FsList sub;
            try
            {
                await WithReconnectAsync(() => dst.MkdirAsync(dstPath, ct), ct);
                sub = await WithReconnectAsync(() => src.ListAsync(srcPath, ct), ct);
            }
            catch (Exception ex) when (IsItemError(ex, ct)) { Fail(ex); return; }
            foreach (var child in sub.Entries)
            {
                if (SafeChild.Of(dstPath, child.Name) is not { } childDst || SafeChild.Of(srcPath, child.Name) is not { } childSrc)
                { _skipped++; continue; }
                await CopyEntryAsync(src, childSrc, dst, childDst, child, depth + 1, ct);
            }
        }
        else
        {
            bool written = false;   // the target was opened for writing: a file already there is gone by then
            try
            {
                await WithReconnectAsync(async () =>
                {
                    StartFileProgress(entry.Size);
                    await using var s = await src.OpenReadAsync(srcPath, ct);
                    await using var ps = new ProgressStream(s, copied => Interlocked.Exchange(ref _fileCopied, copied));
                    written = true;
                    await dst.WriteAsync(dstPath, ps, ct);
                }, ct);
            }
            catch (Exception ex)
            {
                if (written) await DropPartialAsync(dst, dstPath);
                if (!IsItemError(ex, ct)) throw;
                Fail(ex);
            }
        }
    }

    /// <summary>An error that belongs to the one item, not to the whole copy (cancel, a session lost for good).</summary>
    private static bool IsItemError(Exception ex, CancellationToken ct) =>
        !(ex is OperationCanceledException && ct.IsCancellationRequested) && !FileClient.IsConnectionLost(ex);

    private void Fail(Exception ex) { _failed++; _lastError = ex.Message; }

    /// <summary>
    /// A download that broke off after the write began leaves a half-written file here (opening it for writing
    /// emptied whatever was there): it goes. An upload needs nothing: the device writes to a side file and drops
    /// it on failure, and deleting the target there would take the file the operator still has.
    /// </summary>
    private static async Task DropPartialAsync(IFsBackend dst, string path)
    {
        if (dst is not LocalBackend) return;
        try { await dst.DeleteAsync(path, CancellationToken.None); } catch { /* best effort */ }
    }

    private async Task MkdirAsync()
    {
        if (string.IsNullOrEmpty(_active.Path)) { SetStatus(L.FileManager_PickDestDir); return; }
        var name = Prompt(L.FileManager_NewFolder, "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _active.Backend.MkdirAsync(System.IO.Path.Combine(_active.Path, name), _cts.Token);
        await _active.RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        var items = _active.SelectedEntries();
        if (items.Count == 0) return;
        if (MessageBox.Show(L.Format(L.FileManager_ConfirmDelete, items.Count), L.FileManager_Delete, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var (_, path) in items) await _active.Backend.DeleteAsync(path, _cts.Token);
        await _active.RefreshAsync();
    }

    private async Task RenameAsync()
    {
        var items = _active.SelectedEntries();
        if (items.Count != 1) { SetStatus(L.FileManager_SelectOneToRename); return; }
        var (entry, path) = items[0];
        var name = Prompt(L.FileManager_Rename, entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        await _active.Backend.RenameAsync(path, System.IO.Path.Combine(_active.Path, name), _cts.Token);
        await _active.RefreshAsync();
    }

    private static string? Prompt(string title, string def)
    {
        using var f = new Form { Text = title, Width = 400, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, ShowIcon = false };
        var tb = new TextBox { Left = 12, Top = 18, Width = 360, Text = def };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 216, Top = 60, Width = 75 };
        var cancel = new Button { Text = L.FileManager_Cancel, DialogResult = DialogResult.Cancel, Left = 297, Top = 60, Width = 75 };
        f.Controls.AddRange([tb, ok, cancel]); f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }
}

/// <summary>Read-only stream wrapper that reports bytes read so far (for copy progress).</summary>
internal sealed class ProgressStream(Stream inner, Action<long> report) : Stream
{
    private long _read;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        if (n > 0) { _read += n; report(_read); }
        return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int n = await inner.ReadAsync(buffer, ct);
        if (n > 0) { _read += n; report(_read); }
        return n;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
}
