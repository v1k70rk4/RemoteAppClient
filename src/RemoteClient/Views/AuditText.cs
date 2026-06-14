using L = RemoteClient.Localization.Strings;
namespace RemoteClient.Views;

/// <summary>
/// Localizes audit event keys for display. The server stores language-neutral keys such as
/// "connect"; this layer maps them to the current UI language.
/// </summary>
internal static class AuditText
{
    public static string Hu(string action) => action switch
    {
        "connect" => L.AuditText_001,
        "connect-auto" => L.AuditText_002,
        "access-denied" => L.AuditText_003,
        "access-timeout" => L.AuditText_004,
        "access-no-user" => L.AuditText_005,
        "access-locked" => L.AuditText_006,
        "device.enrolled" => L.AuditText_007,
        "device-update" => L.AuditText_008,
        "device-unlock" => L.AuditText_009,
        "device-locked" => L.AuditText_010,
        "login-failed" => L.AuditText_011,
        "user-create" => L.AuditText_012,
        "user-update" => L.AuditText_013,
        "user-reset-password" => L.AuditText_014,
        "user-password-reset-self" => L.AuditText_015,
        "user-totp-clear" => L.AuditText_016,
        "password-code-requested" => L.AuditText_017,
        "password-code-failed" => L.AuditText_018,
        "password-reset-failed" => L.AuditText_019,
        "user-revoke-sessions" => L.AuditText_020,
        "rollout" => "Rollout",
        "promote" => L.AuditText_021,
        "package-upload" => L.AuditText_022,
        "msi-build" => L.AuditText_023,
        "bootstrap-create" => L.AuditText_024,
        "token-revoke" => L.AuditText_029,
        "token-delete" => L.AuditText_025,
        "token-edit" => L.AuditText_026,
        "settings-update" => L.AuditText_027,
        "settings-test-email" => L.AuditText_028,
        _ => action,
    };

    /// <summary>
    /// Localizes the audit detail: the server stores language-neutral reason keys (e.g. "bad_password")
    /// separated by " · " from dynamic data; this maps known reason keys to the current UI language.
    /// </summary>
    public static string Detail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "—";
        var parts = detail.Split([" · "], StringSplitOptions.None);
        for (int i = 0; i < parts.Length; i++) parts[i] = Reason(parts[i]);
        return string.Join(" · ", parts);
    }

    private static string Reason(string segment) => segment switch
    {
        "unknown_user" => L.AuditReason_UnknownUser,
        "bad_password" => L.AuditReason_BadPassword,
        "bad_totp" => L.AuditReason_BadTotp,
        "email_mismatch" => L.AuditReason_EmailMismatch,
        "bad_token" => L.AuditReason_BadToken,
        _ => segment,
    };

    /// <summary>Denial/blocking event shown in red.</summary>
    public static bool IsNegative(string action) =>
        action is "access-denied" or "access-timeout" or "access-no-user" or "access-locked"
            or "password-code-failed" or "password-reset-failed" or "login-failed" or "device-locked";

    /// <summary>Successful access without consent, highlighted in orange.</summary>
    public static bool IsNoConsent(string action) => action == "connect-auto";
}
