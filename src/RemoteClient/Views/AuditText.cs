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
        "connect" => L.AuditText_ConnectionWithConsent,
        "connect-auto" => L.AuditText_ConnectionWithoutConsent,
        "access-denied" => L.AuditText_AccessDenied,
        "access-timeout" => L.AuditText_NoResponseTimeout,
        "access-no-user" => L.AuditText_NoSignedInUser,
        "access-locked" => L.AuditText_DisabledDeviceLocalLock,
        "device.enrolled" => L.AuditText_DeviceEnrolled,
        "device-update" => L.AuditText_DeviceUpdated,
        "device-unlock" => L.AuditText_SignInLockCleared,
        "device-locked" => L.AuditText_DeviceSignInLocked,
        "login-failed" => L.AuditText_FailedSignIn,
        "user-create" => L.AuditText_UserCreated,
        "user-update" => L.AuditText_UserUpdated,
        "user-reset-password" => L.AuditText_PasswordReset,
        "user-password-reset-self" => L.AuditText_PasswordRecovered,
        "user-totp-clear" => L.AuditText_TOTPCleared,
        "password-code-requested" => L.AuditText_RecoveryTokenRequested,
        "password-code-failed" => L.AuditText_RecoveryTokenRequestFailed,
        "password-reset-failed" => L.AuditText_InvalidRecoveryToken,
        "user-revoke-sessions" => L.AuditText_ForceSignOutSessionsRevoked,
        "rollout" => "Rollout",
        "promote" => L.AuditText_PromotionChannel,
        "package-upload" => L.AuditText_PackageUploaded,
        "msi-build" => L.AuditText_MSIBuilt,
        "bootstrap-create" => L.AuditText_BootstrapBlobCreated,
        "token-revoke" => L.AuditText_TokenRevoked,
        "token-delete" => L.AuditText_TokenDeleted,
        "token-edit" => L.AuditText_TokenUpdated,
        "settings-update" => L.AuditText_ServerSettingsUpdated,
        "settings-test-email" => L.AuditText_TestEmailSent,
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
