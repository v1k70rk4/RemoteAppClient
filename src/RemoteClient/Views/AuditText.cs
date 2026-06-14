using L = RemoteClient.Localization.Strings;
namespace RemoteClient.Views;

/// <summary>
/// Audit esemény-KULCSOK fordítása megjelenítéshez. A szerver nyelvfüggetlen kulcsokat tárol
/// (pl. "connect"), itt fordítjuk HU-ra. (EN később: egy második switch / nyelv-kapcsoló.)
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
        "token-revoke" => "Token visszavonva",
        "token-delete" => L.AuditText_025,
        "token-edit" => L.AuditText_026,
        "settings-update" => L.AuditText_027,
        "settings-test-email" => L.AuditText_028,
        _ => action,
    };

    /// <summary>Elutasítás/blokk jellegű esemény (pirossal jelezzük).</summary>
    public static bool IsNegative(string action) =>
        action is "access-denied" or "access-timeout" or "access-no-user" or "access-locked"
            or "password-code-failed" or "password-reset-failed" or "login-failed" or "device-locked";

    /// <summary>Hozzájárulás NÉLKÜL történt sikeres belépés — figyelemfelhívó (narancs).</summary>
    public static bool IsNoConsent(string action) => action == "connect-auto";
}
