using System.Text.Json.Serialization;

namespace RemoteAgent.Enrollment;

/// <summary>
/// A device asks for a short-lived SSH certificate for its tunnel key (<c>POST /api/ssh-cert</c>), authenticated
/// with its client certificate. The server signs the key it has on record; a different key is a rotation: the
/// server adopts it and puts the previous one on the bastion's revocation list, so copies of an old key stop
/// working at once rather than when their certificate ends.
/// </summary>
public sealed class SshCertRequest
{
    /// <summary>The public key to certify, one line in OpenSSH form ("ssh-ed25519 AAAA... comment").</summary>
    [JsonPropertyName("sshPublicKey")] public string SshPublicKey { get; set; } = string.Empty;

    /// <summary>On a rotation, the key the device used until now, so the server can revoke it even when its own
    /// record of the device has none. Honoured only together with <see cref="PreviousKeyProof"/>.</summary>
    [JsonPropertyName("previousSshPublicKey")] public string? PreviousSshPublicKey { get; set; }

    /// <summary>Proof that the device holds the previous key: an OpenSSH signature (<c>ssh-keygen -Y sign</c>,
    /// namespace <c>remoteagent-rotation</c>) made with it over the new key's "type base64" body plus a newline.
    /// Without a valid proof the server revokes nothing on the device's word, so a device can never have another
    /// device's key revoked.</summary>
    [JsonPropertyName("previousKeyProof")] public string? PreviousKeyProof { get; set; }
}

public sealed class SshCertResponse
{
    /// <summary>The OpenSSH certificate, one line, for <c>id_ed25519-cert.pub</c>.</summary>
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = string.Empty;

    /// <summary>Its end, so the agent can log it without parsing.</summary>
    [JsonPropertyName("validBefore")] public DateTimeOffset ValidBefore { get; set; }

    /// <summary>True when this call replaced the device's key on record (the previous one is revoked).</summary>
    [JsonPropertyName("rotated")] public bool Rotated { get; set; }
}
