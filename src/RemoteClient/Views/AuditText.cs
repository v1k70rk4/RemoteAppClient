using System.Drawing;
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
        "device-delete" => L.DevicesView_Delete,
        "access-available" => L.AuditText_Available,
        "access-no-answer" => L.AuditText_NoResponseTimeout,
        "access-busy" => L.AuditText_Busy,
        "device-power" => L.DeviceCommandsPanel_Title,
        "access-scheduled" => L.DeviceCommandsPanel_Scheduled,
        "access-cancelled" => L.DeviceCommandsPanel_Cancelled,
        "access-logged-out" => L.DeviceCommandsPanel_LoggedOut,
        "access-failed" => L.DeviceCommandsPanel_Failed,
        "access-delivered" => L.AuditText_MessageDelivered,
        "device-message" => L.AuditText_MessageSent,
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
        "restart" => L.DeviceCommandsPanel_Restart,
        "force-restart" => L.DeviceCommandsPanel_ForceRestart,
        "cancel" => L.DeviceCommandsPanel_CancelRestart,
        "logout" => L.DeviceCommandsPanel_Logout,
        _ => segment,
    };

    /// <summary>Denial/blocking event shown in red.</summary>
    public static bool IsNegative(string action) =>
        action is "access-denied" or "access-timeout" or "access-no-answer" or "access-no-user" or "access-locked"
            or "password-code-failed" or "password-reset-failed" or "login-failed" or "device-locked";

    /// <summary>Successful access without consent, highlighted in orange.</summary>
    public static bool IsNoConsent(string action) => action == "connect-auto";

    /// <summary>
    /// A short colored category chip for the redesigned log rows: a terse uppercase tag + a fg/bg pair driven
    /// by the event's severity (denials red, no-consent orange, rollouts purple, good outcomes green, …).
    /// </summary>
    public static (string Tag, Color Fg, Color Bg) Chip(string action)
    {
        var (fg, bg) = Palette(action);
        return (Tag(action), fg, bg);
    }

    private static string Tag(string action) => action switch
    {
        "connect" or "connect-auto" or "access-denied" or "access-timeout" or "access-no-user"
            or "access-no-answer" or "access-busy" or "access-available" or "access-scheduled"
            or "access-cancelled" or "access-logged-out" or "access-failed" or "access-locked" => L.AuditText_TagAccess,
        "device-power" => L.AuditText_TagCommand,
        "device-message" or "access-delivered" => L.AuditText_TagMessage,
        "device.enrolled" or "device-update" or "device-delete" or "device-unlock" or "device-locked"
            or "bootstrap-create" => L.AuditText_TagDevice,
        "login-failed" or "user-create" or "user-update" or "user-reset-password" or "user-password-reset-self"
            or "user-totp-clear" or "user-revoke-sessions" or "password-code-requested"
            or "password-code-failed" or "password-reset-failed" => L.AuditText_TagAuth,
        "rollout" or "promote" or "package-upload" or "msi-build" => L.AuditText_TagUpdate,
        "token-revoke" or "token-delete" or "token-edit" or "settings-update" or "settings-test-email" => L.AuditText_TagSystem,
        _ => L.AuditText_TagEvent,
    };

    private static (Color, Color) Palette(string action)
    {
        if (IsNegative(action)) return (ThemeManager.DangerFg, ThemeManager.DangerBg);
        if (IsNoConsent(action)) return (ThemeManager.WarnFg, ThemeManager.WarnBg);
        return action switch
        {
            "rollout" or "promote" or "package-upload" or "msi-build" => (ThemeManager.BetaFg, ThemeManager.BetaBg),
            "access-available" or "access-scheduled" or "access-cancelled" or "access-logged-out"
                or "access-delivered" or "device.enrolled" or "device-unlock" => (ThemeManager.OkFg, ThemeManager.OkBg),
            "token-revoke" or "token-delete" or "token-edit" or "settings-update" or "settings-test-email"
                => (ThemeManager.Text2, ThemeManager.Panel3),
            _ => (ThemeManager.Accent, ThemeManager.AccentSoft),
        };
    }
}
