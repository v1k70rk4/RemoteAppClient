using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// Beléptetési kérés: a gép a saját kulcsával készített CSR-t küld + a tokent.
/// A privát kulcs SOHA nem hagyja el a gépet — a szerver csak aláírja a CSR-t.
/// </summary>
public sealed class EnrollRequest
{
    /// <summary>Egyszer-használatos beléptető token (nyers; a szerver hash-eli és úgy keresi).</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>A gép tanúsítvány-kérelme (PEM, PKCS#10).</summary>
    [JsonPropertyName("csr")]
    public string Csr { get; set; } = string.Empty;

    /// <summary>A gép neve (telemetriához/listázáshoz). A device-azonosítót a SZERVER osztja.</summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;
}

/// <summary>Sikeres beléptetés válasza: a szerver által osztott azonosító + az aláírt cert + a CA.</summary>
public sealed class EnrollResponse
{
    /// <summary>A szerver által kiosztott stabil device-azonosító (a cert CN-je is ez).</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Az aláírt kliens-tanúsítvány (PEM).</summary>
    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = string.Empty;

    /// <summary>A CA tanúsítványa (PEM) — az agent ezt pinneli a szerver-kapcsolathoz.</summary>
    [JsonPropertyName("caCertificate")]
    public string CaCertificate { get; set; } = string.Empty;
}

/// <summary>Hiba esetén gép-olvasható kód (a kliens lokalizál belőle).</summary>
public sealed class EnrollError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
}
