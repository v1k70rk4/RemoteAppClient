# RemoteAppClient — első indítás (előkészítés)

Ez a leírás egy **nulláról** felálló rendszer beállítását vezeti végig: szerver → első admin →
első bootstrap blob → első gép. A `RemoteServer mint-blob` parancs a végén ellenőrzi, hogy minden a helyén van-e.

## Áttekintés

- **Szerver**: Linux, a `RemoteServer` self-contained (nincs külön .NET runtime). Mellette **MariaDB** (10.11+),
  **nginx** (TLS + reverse proxy), és egy **SSH bastion** (az agentek `ssh -R` reverse tunnelt nyitnak ide).
- **Endpointok** az nginx mögött:
  - `/agent` (WSS, C2), `/api/*`, `/auth/*` — **publikusan** elérhetők (az agentek ezeken jönnek be);
  - `/admin/*` — **csak localhostról** (a kliens a gép SSH-tunneljén át éri el). Ezt az nginx korlátozza.
- **Endpoint-gépek (Windows)**: kell rajtuk **OpenSSH kliens** (ssh.exe + ssh-keygen.exe). Win10 / Server 2019+
  alapból tartalmazza; Server 2016-on kézzel kell (az agent a `System32\OpenSSH` és a `Program Files\OpenSSH`
  helyet is nézi).

---

## 1. MariaDB

```sql
CREATE DATABASE remoteserver CHARACTER SET utf8mb4;
CREATE USER 'remotesrv'@'localhost' IDENTIFIED BY '<eros-jelszo>';
GRANT ALL PRIVILEGES ON remoteserver.* TO 'remotesrv'@'localhost';
FLUSH PRIVILEGES;
```

Séma betöltése (a teljes 1.5.0 baseline egy fájlban):

```bash
sudo mysql remoteserver < src/RemoteServer/Data/Migrations/schema.sql
```

## 2. Kulcsok (`/etc/remoteserver`, csak a `remotesrv` user olvashatja)

**Kötelező, kézzel generálni** (a szerver hibát dob, ha hiányzik):

```bash
sudo mkdir -p /etc/remoteserver

# Parancs-aláíró kulcs (ECDSA P-256). Az agentek a publikus párját pinnelik.
sudo openssl ecparam -name prime256v1 -genkey -noout -out /etc/remoteserver/command_signing.key

# Kliens-tanúsítvány CA (a szerver ezzel írja alá az agent certeket)
sudo openssl ecparam -name prime256v1 -genkey -noout -out /etc/remoteserver/ca.key
sudo openssl req -x509 -new -key /etc/remoteserver/ca.key -days 3650 \
    -subj "/CN=RemoteAppClient CA" -out /etc/remoteserver/ca.crt

sudo chown -R remotesrv:remotesrv /etc/remoteserver
sudo chmod 600 /etc/remoteserver/*.key
```

**Automatikusan generálódik** induláskor, ha hiányzik (csak a jogosultságra figyelj):
`secret.key` (DB-titkok titkosítása) és `agent_ca` (SSH CA a tunnelhez).

## 3. `appsettings` (a `RemoteServer` mellett vagy env-ből)

```jsonc
{
  "ConnectionStrings": { "MariaDb": "Server=localhost;Database=remoteserver;User=remotesrv;Password=<jelszo>;" },
  "Server": {
    "PublicUrl": "https://racd.example.com",          // a blobba ágyazott URL
    "CommandSigningKeyPath": "/etc/remoteserver/command_signing.key",
    "CaCertPath": "/etc/remoteserver/ca.crt",
    "CaKeyPath":  "/etc/remoteserver/ca.key",
    "SecretKeyPath": "/etc/remoteserver/secret.key",
    "PackagesDir": "/var/lib/remoteserver/packages",
    "MinClientVersion": "1.1.1.0",
    "Bastion": {
      "Host": "racd.example.com",                     // ahova az agent ssh -R nyit
      "Port": 22,
      "User": "agent",
      "HostKey": "ssh-ed25519 AAAA...",               // a bastion host kulcsa (pinninghez), comment nélkül
      "SshCaKeyPath": "/etc/remoteserver/agent_ca",
      "TunnelPortMin": 50000, "TunnelPortMax": 60000
    }
  }
}
```

> A connection string és a titkok mehetnek env-ből is (`ConnectionStrings__MariaDb=...`), ahogy a systemd unit beállítja.

## 4. SSH bastion

- Egy korlátozott `agent` user, amihez az agentek az **SSH CA-val aláírt** kulcsukkal csatlakoznak és **csak reverse
  port-forwardot** kapnak (`ssh -R`). A bastion `sshd_config`-ban a `TrustedUserCAKeys` az `agent_ca.pub`-ra mutasson.
- A bastion **host kulcsát** tedd a `Bastion:HostKey`-be (pinning) — `ssh-keyscan <host>` adja a sort.

## 5. nginx

- TLS termináció, majd proxy a `RemoteServer`-hez (pl. `127.0.0.1:5000`).
- `/agent` WebSocket-upgrade-del.
- **`/admin/` csak localhostról** (`allow 127.0.0.1; deny all;`) — a kliens a gép SSH-tunneljén át éri el; kívülről nem.
- `/api/`, `/auth/`, `/` publikus.

## 6. systemd

A `RemoteServer` fusson `remotesrv` userként, a connection stringgel az `Environment`-ben, `WorkingDirectory` az install dir.

---

## 7. Első admin

Az **első indításkor** a szerver legseedeli a `admin` usert egy **ideiglenes jelszóval**, amit a logba ír:

```bash
journalctl -u remoteserver | grep BOOTSTRAP
```

(A `mint-blob` is legseedeli, ha még nincs user.) Az első bejelentkezéskor jelszócsere + TOTP kötelező.

## 8. Első bootstrap blob — `mint-blob`

A kliens még nem használható (nincs enrollolt agent a tunnelhez), ezért az **első blobot a szerveren** generáljuk.
Ugyanazzal a konfiggal (connection string!) futtasd, mint a service:

```bash
sudo -u remotesrv ConnectionStrings__MariaDb="Server=localhost;Database=remoteserver;User=remotesrv;Password=<jelszo>;" \
    /opt/remoteserver/RemoteServer mint-blob
```

A parancs **leellenőrzi** a feltételeket (DB+séma, admin, parancs-aláíró kulcs, CA, PublicUrl, bastion), és ha minden
megvan, **kiírja a blobot**. Ha valami hiányzik, `!!`-vel jelzi és nem generál.

## 9. Első gép enrollálása

A blobbal két út:
- **MSI**: a kliensben/admin API-ból gyártott MSI a blobot beágyazza → telepítéskor a gép Pending-be enrollál.
- **Kézzel** egy gépen: `RemoteAgent.exe bootstrap "<blob>"` (majd a service indulásakor self-enroll).

A gép **Pending**-be kerül (a blob `autoApprove=false`). Hagyd jóvá az admin felületen, utána csatlakozhatsz.

---

## Gyors ellenőrző lista

- [ ] MariaDB db + user + `schema.sql`
- [ ] `command_signing.key`, `ca.crt`+`ca.key` generálva (`/etc/remoteserver`, `remotesrv` tulaj)
- [ ] `appsettings`: connection string, `PublicUrl`, kulcs-utak, `Bastion`
- [ ] bastion sshd (agent CA + host key pinning)
- [ ] nginx (`/admin` csak localhost, `/agent` WSS)
- [ ] systemd fut, `journalctl`-ben az admin temp jelszó
- [ ] `RemoteServer mint-blob` → minden `OK`, blob kiírva
- [ ] első gépen OpenSSH kliens (Server 2016-on kézzel)
