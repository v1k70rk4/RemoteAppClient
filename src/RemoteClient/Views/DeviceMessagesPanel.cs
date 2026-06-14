using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device Messages tab: ask the user "is the machine free now?" and send a plain message.</summary>
public sealed class DeviceMessagesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly Func<Task>? _connect;
    private readonly bool _consentRequired;
    private readonly MaterialButton _ask = ViewUi.ToolbarButton(L.DeviceMessagesPanel_AskAvailability);
    private readonly MaterialMultiLineTextBox2 _text = new() { Hint = L.DeviceMessagesPanel_MessageHint, Width = 380, Height = 90 };
    private readonly MaterialButton _send = ViewUi.ToolbarButton(L.DeviceMessagesPanel_Send, primary: false);
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(4, 12, 0, 0) };

    /// <param name="connect">Invoked when the user answers "yes" — connects to the device right away.</param>
    public DeviceMessagesPanel(AdminApi api, DeviceInfo d, Func<Task>? connect = null)
    {
        _api = api; _deviceId = d.DeviceId; _connect = connect;
        _consentRequired = d.ConsentRequired ?? false;
        Dock = DockStyle.Fill;

        _ask.Click += async (_, _) => await AskAsync();
        _send.Click += async (_, _) => await SendAsync();

        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 8) };
        void Lbl(string t) => body.Controls.Add(new MaterialLabel { Text = t, FontType = MaterialSkin.MaterialSkinManager.fontType.Caption, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(4, 10, 0, 0) });

        Lbl(L.DeviceMessagesPanel_AvailabilityHelp);
        body.Controls.Add(_ask);
        body.Controls.Add(new MaterialDivider { Width = 420, Margin = new Padding(4, 16, 4, 8) });
        Lbl(L.DeviceMessagesPanel_MessageHint);
        body.Controls.Add(_text);
        body.Controls.Add(_send);
        body.Controls.Add(_status);
        Controls.Add(body);
    }

    private async Task AskAsync()
    {
        _ask.Enabled = false;
        try
        {
            var outcome = await PollWithCountdownAsync(await _api.AskAvailabilityAsync(_deviceId), 30);

            // "available" = explicit yes. "no-answer"/"no-user" = nobody is at the machine: the user is not
            // present, so connect anyway — unless consent is required, in which case no answer means rejection.
            // "busy" = the user is there and declined → never connect.
            bool connect = outcome == "available"
                || (!_consentRequired && outcome is "no-answer" or "no-user" or "");

            if (connect)
            {
                _status.Text = L.DeviceMessagesPanel_AvailableConnecting;
                if (_connect is not null) await _connect();
                return;
            }

            _status.Text = outcome switch
            {
                "busy" => L.DeviceMessagesPanel_Busy,
                "no-user" => L.DeviceMessagesPanel_NoUser,
                _ => L.DeviceMessagesPanel_NoAnswer, // no answer with consent required → treated as a refusal
            };
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { _ask.Enabled = true; }
    }

    /// <summary>
    /// Polls the access-result while showing a visible countdown (the prompt has a 30s timeout). Returns as
    /// soon as the agent reports an outcome; keeps polling a few seconds past zero to catch the timeout report.
    /// </summary>
    private async Task<string> PollWithCountdownAsync(string? nonce, int seconds)
    {
        if (string.IsNullOrEmpty(nonce)) return "";
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            _status.Text = L.Format(L.DeviceMessagesPanel_WaitingCountdown, remaining);
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* transient */ }
            await Task.Delay(1000);
        }
        for (int i = 0; i < 5; i++) // grace window for the agent to report the timeout
        {
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* transient */ }
            await Task.Delay(1000);
        }
        return "no-answer";
    }

    private async Task SendAsync()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0) return;
        _send.Enabled = false;
        _status.Text = L.DeviceMessagesPanel_Sending;
        try
        {
            var outcome = await PollAsync(await _api.SendMessageAsync(_deviceId, text));
            _status.Text = outcome switch
            {
                "delivered" => L.DeviceMessagesPanel_Delivered,
                "no-user" => L.DeviceMessagesPanel_NoUser,
                _ => L.DeviceMessagesPanel_NoAnswer,
            };
            if (outcome == "delivered") _text.Text = "";
        }
        catch (Exception ex) { _status.Text = L.ForgotPasswordForm_Error + ex.Message; }
        finally { _send.Enabled = true; }
    }

    /// <summary>Polls the access-result for the nonce for ~35s (the prompt has a 30s timeout).</summary>
    private async Task<string> PollAsync(string? nonce)
    {
        if (string.IsNullOrEmpty(nonce)) return "";
        for (int i = 0; i < 35; i++)
        {
            try { var o = await _api.GetAccessResultAsync(nonce); if (!string.IsNullOrEmpty(o)) return o; }
            catch { /* transient */ }
            await Task.Delay(1000);
        }
        return "";
    }
}
