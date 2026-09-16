using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient;

/// <summary>Asks for a name and an expiry for a new read-only access token (Server settings -> Diagnostics).</summary>
public sealed class NewApiTokenForm : MaterialForm
{
    private readonly TextField _name = new(L.ServerSettingsView_TokenNamePlaceholder, 380);
    private readonly UiCombo _scope = new(380);
    private readonly UiCombo _expiry = new(380);
    private readonly Label _warn = new() { AutoSize = true, MaximumSize = new Size(380, 0), Font = UiFont.Small, ForeColor = ThemeManager.WarnFg, BackColor = ThemeManager.Bg, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _hint = new() { AutoSize = true, Font = UiFont.Small, ForeColor = ThemeManager.WarnFg, BackColor = ThemeManager.Bg, Margin = new Padding(0, 8, 0, 0) };

    private sealed record ExpiryItem(int? Days, string Name) { public override string ToString() => Name; }
    private sealed record ScopeItem(string Scope, string Name) { public override string ToString() => Name; }

    public string TokenName { get; private set; } = "";
    public int? ExpiresInDays { get; private set; }
    public string Scope { get; private set; } = ApiTokenScopes.Read;

    public NewApiTokenForm()
    {
        // Not AddFormToManage: it re-themes (greys) the redesigned main window. Material controls self-theme.
        BackColor = ThemeManager.Bg;
        Text = L.ServerSettingsView_TokenNew.TrimEnd('…', '.');
        Sizable = false;
        Width = 460; Height = 440;
        StartPosition = FormStartPosition.CenterParent;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 10, 20, 0), BackColor = ThemeManager.Bg };
        body.Controls.Add(Caption(L.UsersView_Name));
        body.Controls.Add(_name);

        // Read-only is the default; the update scope is a deliberate choice and says out loud what it means.
        body.Controls.Add(Caption(L.ServerSettingsView_TokenScope));
        _scope.Items.AddRange([
            new ScopeItem(ApiTokenScopes.Read, L.ServerSettingsView_TokenScopeRead),
            new ScopeItem(ApiTokenScopes.Update, L.ServerSettingsView_TokenScopeUpdate),
        ]);
        _scope.SelectedIndex = 0;
        _scope.SelectedIndexChanged += (_, _) =>
            _warn.Text = (_scope.SelectedItem as ScopeItem)?.Scope == ApiTokenScopes.Update ? L.ServerSettingsView_TokenScopeWarn : "";
        body.Controls.Add(_scope);
        body.Controls.Add(_warn);

        body.Controls.Add(Caption(L.EditTokenForm_Expiry));
        _expiry.Items.AddRange([
            new ExpiryItem(30, L.Format(L.ServerSettingsView_TokenDays, 30)),
            new ExpiryItem(90, L.Format(L.ServerSettingsView_TokenDays, 90)),
            new ExpiryItem(365, L.Format(L.ServerSettingsView_TokenDays, 365)),
            new ExpiryItem(null, L.ServerSettingsView_TokenNoExpiry),
        ]);
        _expiry.SelectedIndex = 1;
        body.Controls.Add(_expiry);
        body.Controls.Add(_hint);

        var ok = new UiButton(L.ServerSettingsView_TokenCreate) { Margin = new Padding(8, 0, 0, 0) };
        var cancel = new UiButton(L.ConsentWaitForm_Cancel, UiButton.Style.Outline);
        ok.Click += (_, _) =>
        {
            var n = _name.Query;
            if (n.Length == 0) { _hint.Text = L.ServerSettingsView_TokenNameRequired; return; }
            TokenName = n.Length > 64 ? n[..64] : n;
            ExpiresInDays = (_expiry.SelectedItem as ExpiryItem)?.Days;
            Scope = (_scope.SelectedItem as ScopeItem)?.Scope ?? ApiTokenScopes.Read;
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        AcceptButton = ok; CancelButton = cancel;

        // RightToLeft flow: the first control added sits rightmost, so OK ends up at the right edge.
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Height = 56, Padding = new Padding(20, 10, 20, 8), BackColor = ThemeManager.Bg };
        buttons.Controls.AddRange([ok, cancel]);
        Controls.Add(body);
        Controls.Add(buttons);
        Shown += (_, _) => _name.Focus();
    }

    private static Label Caption(string text) => new()
    {
        Text = text, AutoSize = true, Font = UiFont.Label, ForeColor = ThemeManager.Text3, BackColor = ThemeManager.Bg, Margin = new Padding(0, 10, 0, 2),
    };
}

/// <summary>Shows a freshly minted token, once, with a Copy button. There is no second chance to see it: the
/// server keeps only the hash, so closing this without copying means minting another.</summary>
public sealed class ApiTokenRevealForm : MaterialForm
{
    public ApiTokenRevealForm(ApiTokenCreated created)
    {
        BackColor = ThemeManager.Bg;
        Text = L.ServerSettingsView_TokenCreatedTitle;
        Sizable = false;
        Width = 660; Height = 330;
        StartPosition = FormStartPosition.CenterParent;

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20, 10, 20, 0), BackColor = ThemeManager.Bg };
        body.Controls.Add(new Label
        {
            Text = L.ServerSettingsView_TokenShowOnce, AutoSize = true, MaximumSize = new Size(590, 0),
            Font = UiFont.Body, ForeColor = ThemeManager.Text, BackColor = ThemeManager.Bg, Margin = new Padding(0, 0, 0, 12),
        });
        var box = new TextBox
        {
            Text = created.Token, ReadOnly = true, Font = new Font("Consolas", 11F), Width = 590,
            BorderStyle = BorderStyle.FixedSingle, BackColor = ThemeManager.Panel, ForeColor = ThemeManager.Text,
        };
        body.Controls.Add(box);
        var scope = created.Scope == ApiTokenScopes.Update ? L.ServerSettingsView_TokenScopeUpdateShort : L.ServerSettingsView_TokenScopeReadShort;
        body.Controls.Add(new Label
        {
            Text = created.Name + "  ·  " + scope + "  ·  " + (created.ExpiresAt is { } x ? x.LocalDateTime.ToString("g") : L.ServerSettingsView_TokenNoExpiry),
            AutoSize = true, Font = UiFont.Small, ForeColor = ThemeManager.Text3, BackColor = ThemeManager.Bg, Margin = new Padding(0, 6, 0, 0),
        });
        body.Controls.Add(new Label
        {
            Text = L.ServerSettingsView_TokenUsage, AutoSize = true, Font = UiFont.Small, ForeColor = ThemeManager.Text2, BackColor = ThemeManager.Bg, Margin = new Padding(0, 10, 0, 0),
        });
        var status = new Label { AutoSize = true, Font = UiFont.Small, ForeColor = ThemeManager.OkFg, BackColor = ThemeManager.Bg, Margin = new Padding(0, 6, 0, 0) };
        body.Controls.Add(status);

        var copy = new UiButton(L.FileManager_Copy) { Margin = new Padding(8, 0, 0, 0) };
        var close = new UiButton(L.Common_Close, UiButton.Style.Outline);
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(created.Token); status.Text = L.ServerSettingsView_DiagCopied; box.Focus(); box.SelectAll(); }
            catch (Exception ex) { status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        };
        close.Click += (_, _) => DialogResult = DialogResult.OK;
        CancelButton = close;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Height = 56, Padding = new Padding(20, 10, 20, 8), BackColor = ThemeManager.Bg };
        buttons.Controls.AddRange([copy, close]);
        Controls.Add(body);
        Controls.Add(buttons);
        Shown += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
