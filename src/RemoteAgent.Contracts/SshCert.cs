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

    /// <summary>The agent will prove the new key at the bastion and call <c>/api/ssh-cert/confirm</c>; the server
    /// then keeps the previous key valid until that confirmation (or a timeout) instead of revoking it at once.
    /// Agents before 2.3.1 do not send it and get the immediate switch.</summary>
    [JsonPropertyName("canConfirm")] public bool CanConfirm { get; set; }
}

/// <summary>The device proved the new key at the bastion: the server revokes the previous key and makes the new one
/// current (<c>POST /api/ssh-cert/confirm</c>).</summary>
public sealed class SshCertConfirm
{
    [JsonPropertyName("sshPublicKey")] public string SshPublicKey { get; set; } = string.Empty;
}

public sealed class SshCertResponse
{
    /// <summary>The OpenSSH certificate, one line, for <c>id_ed25519-cert.pub</c>.</summary>
    [JsonPropertyName("certificate")] public string Certificate { get; set; } = string.Empty;

    /// <summary>Its end, so the agent can log it without parsing.</summary>
    [JsonPropertyName("validBefore")] public DateTimeOffset ValidBefore { get; set; }

    /// <summary>True when this call concerns a key other than the one on record (a rotation).</summary>
    [JsonPropertyName("rotated")] public bool Rotated { get; set; }

    /// <summary>With <see cref="Rotated"/>: the switch waits for the device's confirmation (the previous key is
    /// still the one on record and nothing is revoked yet) - for this many minutes.</summary>
    [JsonPropertyName("confirmWithinMinutes")] public int? ConfirmWithinMinutes { get; set; }
}
