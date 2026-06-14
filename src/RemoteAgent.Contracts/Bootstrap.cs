using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteAgent.Commands;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Bootstrap blob for tokenless self-install: a single copyable string that tells the
/// agent where to enroll (server URL) and with what (site token).
/// The server generates it from its own URL and a long-lived AutoApprove=false token,
/// then the customer embeds it in the installer. The code never hardcodes server data.
/// </summary>
public sealed class BootstrapBlob
{
    /// <summary>Server base URL, for example https://c2.example.com; the agent appends /enroll.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Site/bootstrap token, long-lived and revocable; the device enters Pending with it.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>Optional server TLS/CA pin for self-signed deployments; empty for public CAs.</summary>
    [JsonPropertyName("caPin")]
    public string? CaPin { get; set; }
}

/// <summary>Encodes and decodes the bootstrap blob as base64url(JSON). It is not encrypted; the token is the secret.</summary>
public static class BootstrapCodec
{
    public static string Encode(BootstrapBlob blob)
    {
        var json = JsonSerializer.Serialize(blob, AgentJsonContext.Default.BootstrapBlob);
        return Base64Url(Encoding.UTF8.GetBytes(json));
    }

    public static BootstrapBlob? Decode(string blob)
    {
        var bytes = Base64UrlDecode(blob.Trim());
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize(json, AgentJsonContext.Default.BootstrapBlob);
    }

    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
