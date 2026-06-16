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
    private readonly ComboBox _drives = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _pathLbl = new() { Dock = DockStyle.Top, AutoEllipsis = true, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
    private readonly CancellationToken _ct;
    private bool _suppressDrive;

    public string Path { get; private set; } = "";
    public IFsBackend Backend => _backend;
    public event Action? Activated2;

    public FilePane(IFsBackend backend, string title, CancellationToken ct)
    {
        _backend = backend; _ct = ct; Dock = DockStyle.Fill;
        _list.Columns.Add(title, 240);
        _list.Columns.Add(L.FileManager_Size, 90, HorizontalAlignment.Right);
        _list.Columns.Add(L.FileManager_Modified, 130);
        if (ThemeManager.IsDark) { _list.BackColor = Color.FromArgb(40, 40, 40); _list.ForeColor = Color.Gainsboro; }
        _list.DoubleClick += async (_, _) => await OpenSelectedAsync();
        _list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; await OpenSelectedAsync(); }
            else if (e.KeyCode == Keys.Back) { e.Handled = true; await UpAsync(); }
        };
        _list.Enter += (_, _) => Activated2?.Invoke();
        _list.Resize += (_, _) => FitColumns();
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

    public async Task HomeAsync() { try { SetList(await _backend.HomeAsync(_ct)); } catch (Exception ex) { _pathLbl.Text = "⚠ " + ex.Message; } }
    public Task RefreshAsync() => NavigateAsync(Path);

    public async Task NavigateAsync(string path)
    {
        try { SetList(await _backend.ListAsync(path, _ct)); }
        catch (Exception ex) { _pathLbl.Text = "⚠ " + ex.Message; }
    }

    private void SetList(FsList l)
    {
        Path = l.Path;
        _pathLbl.Text = string.IsNullOrEmpty(l.Path) ? L.FileManager_Drives : l.Path;
        // Reflect the current drive in the combo without re-triggering navigation.
        var root = string.IsNullOrEmpty(l.Path) ? null : System.IO.Path.GetPathRoot(l.Path);
        if (root is not null && _drives.Items.Contains(root) && (_drives.SelectedItem as string) != root)
        { _suppressDrive = true; _drives.SelectedItem = root; _suppressDrive = false; }
        _list.BeginUpdate();
        _list.Items.Clear();
        if (!string.IsNullOrEmpty(l.Path)) _list.Items.Add(new ListViewItem("..") { Tag = null });
        foreach (var e in l.Entries.Where(e => e.IsDir).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new ListViewItem(new[] { "📁 " + e.Name, "", e.Modified == default ? "" : e.Modified.LocalDateTime.ToString("g") }) { Tag = e });
        foreach (var e in l.Entries.Where(e => !e.IsDir).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new ListViewItem(new[] { e.Name, FmtSize(e.Size), e.Modified == default ? "" : e.Modified.LocalDateTime.ToString("g") }) { Tag = e });
        _list.EndUpdate();
        FitColumns();
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
}

/// <summary>Total Commander-style two-pane file manager: local PC (left) ↔ remote device (right).</summary>
public sealed class FileManagerWindow : MaterialForm
{
    private readonly FilePane _left, _right;
    private FilePane _active;
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly MaterialProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 10, Visible = false };
    private readonly Label _progressLbl = new() { Dock = DockStyle.Bottom, Height = 18, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
    private readonly FileClient _fc;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _copyCts; // per-copy, cancellable from the Mégse button / Esc

    public FileManagerWindow(int localPort, string token, string hostname)
    {
        _fc = new FileClient(localPort, token);
        ThemeManager.Skin.AddFormToManage(this);
        Text = $"{L.FileManager_Title} — {hostname}";
        Size = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;

        _left = new FilePane(new LocalBackend(), L.FileManager_LocalPC, _cts.Token);
        _right = new FilePane(new RemoteBackend(_fc), hostname, _cts.Token);
        _active = _left;
        _left.Activated2 += () => _active = _left;
        _right.Activated2 += () => _active = _right;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_left);
        split.Panel2.Controls.Add(_right);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(6) };
        void Btn(string t, Func<Task> a) { var b = new MaterialButton { Text = t, Type = MaterialButton.MaterialButtonType.Outlined, AutoSize = true, Margin = new Padding(4, 4, 4, 4) }; b.Click += async (_, _) => await Guard(a); bar.Controls.Add(b); }
        Btn($"{L.FileManager_Copy} (F5)", CopyAsync);
        Btn($"{L.FileManager_NewFolder} (F7)", MkdirAsync);
        Btn($"{L.FileManager_Delete} (F8)", DeleteAsync);
        Btn($"{L.FileManager_Rename} (F2)", RenameAsync);
        Btn(L.AboutView_Refresh, () => _active.RefreshAsync());
        var cancelBtn = new MaterialButton { Text = $"{L.FileManager_Cancel} (Esc)", Type = MaterialButton.MaterialButtonType.Outlined, AutoSize = true, Margin = new Padding(4, 4, 4, 4) };
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
        FormClosed += (_, _) => { _cts.Cancel(); _fc.Dispose(); };
    }

    private FilePane Other => _active == _left ? _right : _left;
    private void SetStatus(string s) { if (InvokeRequired) BeginInvoke(() => _status.Text = s); else _status.Text = s; }
    private async Task Guard(Func<Task> a) { try { await a(); } catch (Exception ex) { SetStatus(L.ForgotPasswordForm_Error + ex.Message); } }

    private void ShowProgress(bool on)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowProgress(on)); return; }
        _progress.Visible = on; _progressLbl.Visible = on;
        if (!on) { _progress.Value = 0; _progressLbl.Text = ""; }
    }

    private void SetFileProgress(long copied, long total)
    {
        if (InvokeRequired) { BeginInvoke(() => SetFileProgress(copied, total)); return; }
        _progress.Value = total > 0 ? (int)Math.Min(100, copied * 100 / total) : 0;
        _progressLbl.Text = $"{copied / 1024:N0} kB / {(total > 0 ? total : copied) / 1024:N0} kB";
    }

    private async Task CopyAsync()
    {
        var items = _active.SelectedEntries();
        if (items.Count == 0) { SetStatus(L.FileManager_NothingSelected); return; }
        if (string.IsNullOrEmpty(Other.Path)) { SetStatus(L.FileManager_PickDestDir); return; }
        var src = _active.Backend; var dst = Other.Backend; var dstDir = Other.Path;
        _copyCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = _copyCts.Token;
        ShowProgress(true);
        try
        {
            int n = 0;
            foreach (var (entry, path) in items)
            {
                n++;
                SetStatus(L.Format(L.FileManager_Copying, entry.Name, n, items.Count));
                await CopyEntryAsync(src, path, dst, System.IO.Path.Combine(dstDir, entry.Name), entry, ct);
            }
            SetStatus(L.FileManager_Done);
        }
        catch (OperationCanceledException) { SetStatus(L.FileManager_Cancelled); }
        finally { ShowProgress(false); _copyCts.Dispose(); _copyCts = null; }
        await Other.RefreshAsync();
    }

    private async Task CopyEntryAsync(IFsBackend src, string srcPath, IFsBackend dst, string dstPath, FsEntry entry, CancellationToken ct)
    {
        if (entry.IsDir)
        {
            await dst.MkdirAsync(dstPath, ct);
            var sub = await src.ListAsync(srcPath, ct);
            foreach (var child in sub.Entries)
                await CopyEntryAsync(src, System.IO.Path.Combine(srcPath, child.Name), dst, System.IO.Path.Combine(dstPath, child.Name), child, ct);
        }
        else
        {
            SetFileProgress(0, entry.Size);
            long last = 0;
            try
            {
                await using var s = await src.OpenReadAsync(srcPath, ct);
                await using var ps = new ProgressStream(s, copied =>
                {
                    if (copied - last >= 262144 || copied >= entry.Size) { last = copied; SetFileProgress(copied, entry.Size); }
                });
                await dst.WriteAsync(dstPath, ps, ct);
            }
            catch (OperationCanceledException)
            {
                try { await dst.DeleteAsync(dstPath, CancellationToken.None); } catch { /* best effort: drop the partial file */ }
                throw;
            }
        }
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
