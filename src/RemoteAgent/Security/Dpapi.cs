using System.Security.Cryptography;

namespace RemoteAgent.Security;

/// <summary>
/// Windows DPAPI (LocalMachine hatókör) — a titkokat géphez köti: a titkosított
/// blob csak ezen a gépen fejthető vissza, lemásolva máshol használhatatlan.
/// (A SYSTEM service és a beléptető admin ugyanazt a gépet használja, ezért működik.)
/// </summary>
public static class Dpapi
{
    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.LocalMachine);
}
