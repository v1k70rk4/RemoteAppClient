using System.Security.Cryptography;

namespace RemoteAgent.Security;

/// <summary>
/// Windows DPAPI with LocalMachine scope. Secrets are bound to the device: encrypted
/// blobs can only be decrypted on this machine and are unusable when copied elsewhere.
/// This works because the SYSTEM service and enrolling admin use the same machine scope.
/// </summary>
public static class Dpapi
{
    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.LocalMachine);
}
