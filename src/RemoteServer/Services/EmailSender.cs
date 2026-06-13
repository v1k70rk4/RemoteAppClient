using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RemoteServer.Data;
using RemoteServer.Security;

namespace RemoteServer.Services;

public interface IEmailSender
{
    /// <summary>Levelet küld az aktív providerrel (SMTP vagy Graph). (ok, error) — error null, ha sikeres.</summary>
    Task<(bool Ok, string? Error)> SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>
/// E-mail küldés a szerver-beállítások szerint: SMTP (System.Net.Mail) vagy
/// MS Graph app-only (client credentials → /sendMail). A titkokat SecretProtector fejti vissza.
/// </summary>
public sealed class EmailSender(AppDbContext db, SecretProtector protector, ILogger<EmailSender> logger) : IEmailSender
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<(bool Ok, string? Error)> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to)) return (false, "Hiányzó címzett.");
        var s = await db.ServerSettings.FirstOrDefaultAsync(ct);
        if (s is null) return (false, "Nincs e-mail beállítás.");

        try
        {
            return s.EmailProvider switch
            {
                "smtp"  => await SendSmtpAsync(s, to, subject, body, ct),
                "graph" => await SendGraphAsync(s, to, subject, body, ct),
                _       => (false, "Nincs aktív e-mail provider beállítva."),
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "E-mail küldés hiba ({Provider}).", s.EmailProvider);
            return (false, ex.Message);
        }
    }

    private async Task<(bool, string?)> SendSmtpAsync(Data.Entities.ServerSettings s, string to, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) return (false, "Hiányzó SMTP host.");
        var from = string.IsNullOrWhiteSpace(s.SmtpFrom) ? s.SmtpUser : s.SmtpFrom;
        if (string.IsNullOrWhiteSpace(from)) return (false, "Hiányzó feladó (SmtpFrom/SmtpUser).");

        using var msg = new MailMessage(from!, to, subject, body) { IsBodyHtml = false };
        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort) { EnableSsl = s.SmtpUseTls };
        var pw = protector.TryUnprotect(s.SmtpPasswordEnc);
        if (!string.IsNullOrWhiteSpace(s.SmtpUser))
            client.Credentials = new NetworkCredential(s.SmtpUser, pw ?? "");

        await client.SendMailAsync(msg, ct);
        return (true, null);
    }

    private async Task<(bool, string?)> SendGraphAsync(Data.Entities.ServerSettings s, string to, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.GraphTenantId) || string.IsNullOrWhiteSpace(s.GraphClientId) || string.IsNullOrWhiteSpace(s.GraphSender))
            return (false, "Hiányzó Graph beállítás (tenant/client/sender).");
        var secret = protector.TryUnprotect(s.GraphClientSecretEnc);
        if (string.IsNullOrWhiteSpace(secret)) return (false, "Hiányzó Graph client secret.");

        // 1) token — client credentials
        using var tokenReq = new HttpRequestMessage(HttpMethod.Post,
            $"https://login.microsoftonline.com/{s.GraphTenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = s.GraphClientId!,
                ["client_secret"] = secret!,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            }),
        };
        using var tokenResp = await Http.SendAsync(tokenReq, ct);
        var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
        if (!tokenResp.IsSuccessStatusCode) return (false, $"Token hiba ({(int)tokenResp.StatusCode}): {Trim(tokenJson)}");

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var atEl) || atEl.GetString() is not { } accessToken)
            return (false, "A token-válasz nem tartalmaz access_token-t.");

        // 2) sendMail
        var payload = BuildSendMailJson(to, subject, body);
        using var mailReq = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(s.GraphSender!)}/sendMail")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        mailReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var mailResp = await Http.SendAsync(mailReq, ct);
        if (mailResp.IsSuccessStatusCode) return (true, null);
        var err = await mailResp.Content.ReadAsStringAsync(ct);
        return (false, $"sendMail hiba ({(int)mailResp.StatusCode}): {Trim(err)}");
    }

    private static string BuildSendMailJson(string to, string subject, string body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("message");
            w.WriteString("subject", subject);
            w.WriteStartObject("body");
            w.WriteString("contentType", "Text");
            w.WriteString("content", body);
            w.WriteEndObject();
            w.WriteStartArray("toRecipients");
            w.WriteStartObject();
            w.WriteStartObject("emailAddress");
            w.WriteString("address", to);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteBoolean("saveToSentItems", false);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
