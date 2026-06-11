# RemoteAppClient

Vékony távelérési rendszer .NET 10-en, C2-modellben. Az agent SYSTEM alatt futó
Windows service, ami kifelé tartja a parancscsatornát, parancsra reverse SSH
tunnelt épít a bástya szerverre (amin keresztül VNC-ztek), és periodikusan
telemetriát küld magáról. A szerver (.NET, Linuxon fut) push-olja az aláírt
parancsokat és fogadja a telemetriát.

A nehéz 80% (a képernyő capture/encode) **nincs** itt — azt a VNC szerver viszi.
Ez a rendszer csak a C2-logika: kapcsolattartás, parancsfigyelés, tunnel, telemetria.

## Solution-struktúra

| Projekt | Mi | Hol fut |
|---|---|---|
| `RemoteAgent.Contracts` | közös parancs-DTO-k + a kanonikus aláírás-forma (`CommandSignature`) | mindkét oldal hivatkozza |
| `RemoteAgent` | a kliens (Windows service) | kliensgépek (SYSTEM) |
| `RemoteServer` | WSS hub + telemetria API + parancs-aláírás + admin | szerver (Linux) |

A `Contracts` a kulcs: a kliens `Verify`-ol, a szerver `Sign`-ol, **ugyanazzal**
a `CommandSignature.Canonicalize` formával — az aláírás definíció szerint nem
csúszhat szét a két oldal közt.

> Tervezett, még meg nem írt darabok: `RemoteAgent.Updater` (self-update),
> `client.exe` (helyi + admin konzol), Postgres-perzisztencia, enrollment
> (token + 2FA) és mini-CA, TightVNC-becsomagolás, `deploy/` scriptek.

## RemoteAgent (kliens)

## Architektúra

A `Host.CreateApplicationBuilder` három `BackgroundService`-t futtat egy
processben:

| Szolgáltatás | Feladat |
|---|---|
| `CommandChannelService` | Kimenő WSS (443) a szerver felé. Fogadja az **aláírt** parancsokat, ellenőrzi (`CommandVerifier`), a jókat a `CommandBus`-ra teszi. Lekapcsolódásnál exponenciális backoff. |
| `TunnelOrchestratorService` | A busról olvas. `open-tunnel`/`close-tunnel` parancsra `ssh.exe`-vel reverse tunnelt nyit/zár (`SshReverseTunnel`). Idle-timeout után magától lebont. |
| `TelemetryService` | Periodikusan összeállítja (`SystemInfoCollector`) és elküldi a gép-telemetriát az API-ba (mTLS). |

```
Szerver  ──WSS (aláírt parancs)──►  CommandChannelService ──► CommandBus ──► TunnelOrchestrator ──► ssh -R ──► Bástya
                                                                                                              ▲
Ti  ──VNC a bástya portjára──────────────────────────────────────────────────────────────────────────────┘

Agent  ──telemetria (mTLS)──►  /api/telemetry  ──►  SQL  (a DB sosem látszik kintről)
```

A rétegek szándékosan szét vannak húzva (Configuration / Commands / Security /
Tunnel / Telemetry / Services), hogy ne keletkezzen újra a spagetti: minden
darab egy dologért felel, és külön tesztelhető.

## Biztonsági modell

Ez egy internet felé figyelő C2 sok ügyfélgépen, ezért a parancscsatorna
hitelessége nem opcionális:

1. **mTLS** — az agent kliens-tanúsítvánnyal hitelesíti magát, és **pinneli** a
   szerver TLS-tanúsítványát (`ServerCertPinSha256`). Hamis szerverhez nem
   csatlakozik akkor sem, ha a CA-lánc egyébként érvényes lenne.
2. **Aláírt parancsok** — minden parancs ECDSA P-256 aláírást hordoz a szerver
   privát kulcsával; az agent a pinnelt publikus kulccsal (`CommandSigningPublicKey`)
   ellenőrzi. Aláírás nélkül / rossz aláírással → eldobva.
3. **Replay-védelem** — minden parancs `nonce` + `iat` (időbélyeg). Az ablakon
   kívüli vagy már látott parancs eldobva.
4. **Fix tunnel-cél** — a szerver csak a *távoli portszámot* adhatja meg. A
   bástya host, user, kulcs és a forward helyi célja (VNC port) konfigból jön,
   parancsból **nem** felülírható. A bástya host-kulcsa pinnelt → az agent
   máshová nem épít tunnelt.
5. **Telemetria API mögött** — az agent nem ír közvetlenül SQL-be. Egy minimál
   API validál és ír, a DB nincs a támadási felületen.

## Konfiguráció

Minden az `appsettings.json` `Agent` szekciójában (lásd a fájlt). Gépspecifikus
override jöhet környezeti változóból (`Agent__CommandChannel__Url=...`) vagy
Intune-os registry/fájl kitelepítésből. A titkok (privát kulcs, tanúsítványok)
**nem** a repo-ban élnek — store-ban (`LocalMachine\My`) és `C:\ProgramData\RemoteAgent\` alatt.

Amit ki kell tölteni élesítés előtt:

- `CommandChannel.Url`, `ClientCertThumbprint`, `ServerCertPinSha256`, `CommandSigningPublicKey`
- `Tunnel.BastionHost/Port/User`, `PrivateKeyPath`, `BastionHostKey`
- `Telemetry.IngestUrl`, `ClientCertThumbprint`, `ServerCertPinSha256`

## Build és publish

```powershell
# Fejlesztői build
dotnet build src/RemoteAgent/RemoteAgent.csproj -c Release

# Telepítéshez: self-contained single-file exe (runtime nélkül)
dotnet publish src/RemoteAgent/RemoteAgent.csproj -c Release
# -> src/RemoteAgent/bin/Release/net10.0-windows/win-x64/publish/RemoteAgent.exe
```

A kód reflection-mentes (forrásgenerált JSON), így később `<PublishAot>true`
bekapcsolásával NativeAOT-ra is átállítható, ha kisebb/gyorsabb exe kell.

## RemoteServer (szerver)

Parancs-aláíró kulcspár (a privát a szerveré, a publikus SPKI Base64 az agent
`CommandSigningPublicKey` configjába megy):

```bash
openssl ecparam -name prime256v1 -genkey -noout -out cmd_signing.key
openssl pkey -in cmd_signing.key -pubout -outform DER | base64 -w0   # -> agent config
```

Futtatás (a `Server:CommandSigningKeyPath` a privát kulcsra mutasson):

```bash
dotnet run --project src/RemoteServer
```

Végpontok: `GET /` állapot · `/agent` (agent WSS parancscsatorna) ·
`POST /api/telemetry` · `GET /admin/devices` (online gépek) ·
`POST /admin/devices/{id}/open-tunnel?remotePort=N` (aláírt parancs push).
Éles üzemben nginx terminálja a TLS-t és validálja a kliens-certet (mTLS); a
device-azonosító a cert CN-jéből jön, fallback a `?deviceId=`.

### Adatbázis (MariaDB / Galera)

EF Core 9 + Pomelo provider (alatta `MySqlConnector`). A séma EF migrationsben
verziózott (`src/RemoteServer/Data/Migrations/`), Galera-tudatosan: minden
táblának van PK-ja, `EnableRetryOnFailure` a tranziens cluster-hibákra.

Táblák: `Users`/`Roles`/`UserRoles` · `EnrollmentTokens` · `DeviceGroups`/`Devices`/`DeviceTelemetry`
· `Commands` (egységes parancs-sor) · `RemoteSessions` · `AuditLogs`.

```bash
# Connection string env-ből (ne a repóból): ConnectionStrings__MariaDb="Server=...;Database=remoteserver;User Id=...;Password=..."
dotnet ef database update --project src/RemoteServer    # séma létrehozása éles DB-n
```

A `Data/Migrations/schema.sql` egy generált, ember által olvasható pillanatkép a sémáról (a migrációk a forrás).

## Telepítés (Intune)

1. A publish exe + `appsettings.json` becsomagolása MSI-be (pl. WiX), vagy
   Win32 app `.intunewin`-be.
2. Service regisztráció telepítéskor:
   ```powershell
   sc.exe create RemoteAgent binPath= "C:\Program Files\RemoteAgent\RemoteAgent.exe" start= auto obj= LocalSystem
   sc.exe start RemoteAgent
   ```
3. A kliens-tanúsítvány a `LocalMachine\My` store-ba, az SSH privát kulcs
   `C:\ProgramData\RemoteAgent\`-be (csak SYSTEM-nek olvasható ACL).
4. **Frissítés Intune-ról**, ne legyen self-update (kevesebb támadási felület).

## VNC oldal

A képet kész VNC szerver viszi (UltraVNC / TigerVNC), **SYSTEM service-ként**,
hogy a login képernyő és a secure desktop (UAC prompt) is elérhető legyen
unattended módban. Ez nem ennek a repo-nak a része.

## Helyi futtatás (debug)

```powershell
dotnet run --project src/RemoteAgent
```

Konfigurált szerver nélkül a szolgáltatások tétlenek maradnak és ezt logolják —
nem hibázik le. Konzolból futtatva a logok a konzolra is mennek.
