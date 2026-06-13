namespace RemoteClient.Views;

/// <summary>
/// Audit esemény-KULCSOK fordítása megjelenítéshez. A szerver nyelvfüggetlen kulcsokat tárol
/// (pl. "connect"), itt fordítjuk HU-ra. (EN később: egy második switch / nyelv-kapcsoló.)
/// </summary>
internal static class AuditText
{
    public static string Hu(string action) => action switch
    {
        "connect" => "Csatlakozás (hozzájárulással)",
        "connect-auto" => "Csatlakozás (hozzájárulás nélkül)",
        "access-denied" => "Hozzáférés elutasítva",
        "access-timeout" => "Nincs válasz (timeout)",
        "access-no-user" => "Nincs bejelentkezett felhasználó",
        "access-locked" => "Letiltott gép (helyi zár)",
        "device.enrolled" => "Gép beléptetve",
        "device-update" => "Eszköz módosítva",
        "device-unlock" => "Belépés-zárolás feloldva",
        "device-locked" => "Gép belépés-zárolva",
        "login-failed" => "Sikertelen belépés",
        "user-create" => "Felhasználó létrehozva",
        "user-update" => "Felhasználó módosítva",
        "user-reset-password" => "Jelszó resetelve",
        "user-password-reset-self" => "Jelszó helyreállítva",
        "user-totp-clear" => "TOTP törölve",
        "password-code-requested" => "Helyreállítási token kérve",
        "password-code-failed" => "Helyreállítási token kérése sikertelen",
        "password-reset-failed" => "Hibás helyreállítási token",
        "user-revoke-sessions" => "Kiléptetés (sessionök törölve)",
        "rollout" => "Rollout",
        "promote" => "Promótálás (csatorna)",
        "package-upload" => "Csomag feltöltve",
        "msi-build" => "MSI gyártva",
        "bootstrap-create" => "Bootstrap blob létrehozva",
        "token-revoke" => "Token visszavonva",
        "token-delete" => "Token törölve",
        "token-edit" => "Token módosítva",
        "settings-update" => "Szerver beállítások módosítva",
        "settings-test-email" => "Teszt e-mail küldve",
        _ => action,
    };

    /// <summary>Elutasítás/blokk jellegű esemény (pirossal jelezzük).</summary>
    public static bool IsNegative(string action) =>
        action is "access-denied" or "access-timeout" or "access-no-user" or "access-locked"
            or "password-code-failed" or "password-reset-failed" or "login-failed" or "device-locked";

    /// <summary>Hozzájárulás NÉLKÜL történt sikeres belépés — figyelemfelhívó (narancs).</summary>
    public static bool IsNoConsent(string action) => action == "connect-auto";
}
