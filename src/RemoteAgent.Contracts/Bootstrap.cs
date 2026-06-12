using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteAgent.Commands;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Token nélküli ön-telepítés "bootstrap blobja": egyetlen, bemásolható string, ami
/// megmondja az agentnek HOVÁ (szerver-URL) és MIVEL (site-token) léptessen be.
/// A szerver generálja (a saját URL-jéből + egy long-lived, AutoApprove=false tokenből),
/// az ügyfél a telepítőbe teszi. A kód SOHA nem tartalmaz szerver-adatot — az itt utazik.
/// </summary>
public sealed class BootstrapBlob
{
    /// <summary>A szerver bázis-URL-je (pl. https://c2.pelda.hu), amihez az agent /enroll-t fűz.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>A site/bootstrap token (long-lived, visszavonható; a gép Pending-be kerül vele).</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>Opcionális: a szerver TLS/CA pin (self-signed esetén); publikus CA-nál üres.</summary>
    [JsonPropertyName("caPin")]
    public string? CaPin { get; set; }
}

/// <summary>A bootstrap blob be-/kikódolása: base64url(JSON). Nem titkosít — a token a titok.</summary>
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
