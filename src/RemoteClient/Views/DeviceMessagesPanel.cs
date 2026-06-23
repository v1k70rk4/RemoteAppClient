using System.Drawing;
using MaterialSkin.Controls;
using RemoteAgent.Admin;
using L = RemoteClient.Localization.Strings;

namespace RemoteClient.Views;

/// <summary>Device Messages tab: ask the user "is the machine free now?" and send a plain message —
/// two cards (ask availability / send a message) per design_handoff_console_redesign.</summary>
public sealed class DeviceMessagesPanel : UserControl
{
    private readonly AdminApi _api;
    private readonly string _deviceId;
    private readonly Func<Task>? _connect;
    private readonly bool _consentRequired;
    private readonly UiButton _ask = new(L.DeviceMessagesPanel_AskAvailability, UiButton.Style.Filled, "chat");
    private readonly TextField _text = new(L.DeviceMessagesPanel_MessageHint, 380, multiline: true);
    private readonly UiButton _send = new(L.DeviceMessagesPanel_Send, UiButton.Style.Outline);
    private readonly MaterialLabel _status = new() { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(2, 10, 0, 0) };

    /// <param name="connect">Invoked when the user answers "yes" — connects to the device right away.</param>
    public DeviceMessagesPanel(AdminApi api, DeviceInfo d, Func<Task>? connect = null)
    {
        _api = api; _deviceId = d.DeviceId; _connect = connect;
        _consentRequired = d.ConsentRequired ?? false;
        Dock = DockStyle.Fill;
        BackColor = ThemeManager.Bg;
        Padding = new Padding(16);

        _ask.Click += async (_, _) => await AskAsync();
        _send.Click += async (_, _) => await SendAsync();

        const int cardW = 540, contentW = cardW - 36;

        var askBody = new Panel();
        _ask.Location = new Point(0, 0);
        askBody.Controls.Add(_ask);
        var askCard = new Card(L.DeviceMessagesPanel_AskTitle, L.DeviceMessagesPanel_AvailabilityHelp, askBody)
            { Width = cardW, Height = 66 + 38 + 16, Margin = new Padding(0, 0, 0, 16) };

        var sendBody = new Panel();
        _text.SetBounds(0, 0, contentW, 92);
        _send.Location = new Point(contentW - _send.Width, 104);
        sendBody.Controls.Add(_text);
        sendBody.Controls.Add(_send);
        var sendCard = new Card(L.DeviceMessagesPanel_SendTitle, null, sendBody)
            { Width = cardW, Height = 46 + 92 + 12 + 38 + 16, Margin = new Padding(0) };

        var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        stack.Controls.Add(askCard);
        stack.Controls.Add(sendCard);
        stack.Controls.Add(_status);
        Controls.Add(stack);
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

            // Declined ("No"), or no answer while consent is required: ask the user (if one is present)
            // to call back when free. Nobody to tell when there is no signed-in user.
            if (outcome != "no-user")
                try { await _api.SendMessageAsync(_deviceId, L.DeviceMessagesPanel_CallWhenFree); } catch { /* best effort */ }
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
