namespace RemoteAgent.Security;

/// <summary>When the agent asks the server for a new key and certificate. Pure, so it is testable.</summary>
public static class RekeyPolicy
{
    /// <summary>A certificate this close to its end is renewed. Half a year, because a device can sit switched
    /// off for a whole summer break and must still have a valid certificate to renew with when it returns; the
    /// server's health alert at 60 days then only names devices that failed to renew in time.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(180);

    /// <summary>"tpm" when the key is not in the TPM but could be; "renewal" when the certificate is near its
    /// end; null when nothing is due. The TPM move comes first: it also renews.</summary>
    public static string? Reason(string provider, bool tpmUsable, DateTimeOffset? notAfter, DateTimeOffset now)
    {
        if (provider != "tpm" && tpmUsable) return "tpm"; // DeviceKeyStore.Tpm; a literal so this file stands alone in the tests
        if (notAfter is { } end && end - now < RenewBefore) return "renewal";
        return null;
    }
}
