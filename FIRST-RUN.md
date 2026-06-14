# 🚀 RemoteAppClient — első indítás

Nulláról felálló rendszer beállítása **a `deploy/` szkriptekkel**: szerver → első admin →
első bootstrap blob → első gép. A végén a `RemoteServer mint-blob` **leellenőrzi**, hogy minden a helyén van-e.

A szkriptek **idempotensek** (újrafuttathatók), és a titkokat a gépen generálják — semmi nem kerül a repóba.

---

## 🧭 Áttekintés

- **Szerver**: Linux + **MariaDB** (10.11+) + **nginx** (TLS, reverse proxy) + **SSH bástya**. A `RemoteServer` self-contained.
- **Endpointok** az nginx mögött: `/agent` (WSS), `/api/*`, `/auth/*` **publikus**; `/admin/*` **csak localhost**
  (a kliens a gép SSH-tunneljén át éri el — ezt a `setup-nginx.sh` `allow 127.0.0.1; deny all`-lal állítja be).
- **Endpoint-gépek (Windows)**: az MSI mindent feltesz; **OpenSSH kliens** kell (Win10/Server 2019+ alapból; Server 2016-on kézzel).

---

## 🖥️ Szerver beállítása (sorrendben)

Másold a `deploy/` mappát és a `schema.sql`-t a szerverre, majd:

### 1) Adatbázis
```bash
deploy/setup-db.sh
# -> létrehozza a 'remoteserver' DB-t + usert, és a connection stringet ide írja:
#    /etc/remoteserver/db.env  (a jelszó a gépen generálódik)
```

### 2) Séma betöltése (a baseline egy fájlban)
```bash
sudo mariadb remoteserver < src/RemoteServer/Data/Migrations/schema.sql
```

### 3) Szerver telepítése (kulcsokkal, systemddel)
A szerver buildje legyen `/tmp/remoteserver.tar.gz`-ben — ezt a **GitHub Release**
`RemoteServer-linux-x64.tar.gz`-jából is veheted (átnevezve), vagy:
`dotnet publish src/RemoteServer/RemoteServer.csproj -c Release -r linux-x64 --self-contained -o out && tar -C out -czf /tmp/remoteserver.tar.gz .`

```bash
BASTION_HOST=racd.example.com deploy/deploy-server.sh
```
Ez **legenerálja**: parancs-aláíró kulcs (`cmd_signing.key`), kliens-**CA** (`ca.key`/`ca.crt`),
`secret.key`, `bastion.env` (host + host-key), systemd unit (db.env + bastion.env), és **elindítja** a service-t.
A kimenetben **kiírja**:
- 🔑 az agent **`CommandSigningPublicKey`**-ét (ezt az agent configjába kell) és
- a **CA fingerprintet** (a TLS-pinninghez).

### 4) Public URL
A blobhoz/MSI-hez kell a publikus URL (nincs az appsettings-ben). Tedd env-be és indítsd újra:
```bash
echo "Server__PublicUrl=https://racd.example.com" | sudo tee -a /etc/remoteserver/bastion.env
sudo systemctl restart remoteserver
```

### 5) Bástya (reverse tunnel)
```bash
deploy/setup-bastion.sh
# -> 'agent' korlátozott SSH user (csak reverse forward), SSH CA (agent_ca),
#    sshd: TrustedUserCAKeys + GatewayPorts=no (a forward csak a bástya localhostján látszik)
```

### 6) TLS (Let's Encrypt, Cloudflare DNS-01)
Előfeltétel: `/etc/letsencrypt/cloudflare.ini` (szűkített Cloudflare token, `chmod 600`).
```bash
DOMAIN=racd.example.com ACME_EMAIL=admin@example.com deploy/setup-tls.sh
```

### 7) nginx
```bash
DOMAIN=racd.example.com deploy/setup-nginx.sh
# -> TLS, /agent WebSocket, mTLS (kliens-CA), és /admin CSAK localhostról
```

### 8) Hardening
```bash
deploy/harden.sh        # ufw (443 + SSH), fail2ban, sshd (root off, jelszó off)
```

---

## 👤 Első admin

Az **első indításkor** a szerver legseedeli a `admin` usert egy **ideiglenes jelszóval**, amit a logba ír:
```bash
journalctl -u remoteserver | grep BOOTSTRAP
```
(A `mint-blob` is legseedeli, ha még nincs user.) Az első bejelentkezéskor **jelszócsere + TOTP** kötelező.

## 🎟️ Első bootstrap blob — `mint-blob`

A kliens még nem használható (nincs enrollolt agent a tunnelhez), ezért az **első blobot a szerveren** generáljuk.
Ugyanazzal a környezettel futtasd, mint a service (a `db.env`/`bastion.env`-ből):

```bash
sudo systemd-run --quiet --wait --collect --pipe --uid=remotesrv --working-directory=/opt/remoteserver \
  --property=EnvironmentFile=/etc/remoteserver/db.env \
  --property=EnvironmentFile=-/etc/remoteserver/bastion.env \
  /opt/remoteserver/RemoteServer mint-blob
```

A parancs **leellenőrzi** a feltételeket (DB+séma, admin, parancs-aláíró kulcs, CA, PublicUrl, bastion), és ha minden
megvan, **kiírja a blobot**. Ami hiányzik, azt `!!`-vel jelzi és nem generál.

## 💻 Első gép enrollálása

A komponens-csomagokat töltsd fel a szerverre (**GitHub Release** exéi: `agent`/`updater`/`client`,
és a `vnc` a `deploy/fetch-tightvnc.sh`-ból), majd:
- **MSI** (a kliensből / admin API-ból gyártva) — a blobot + a TightVNC-t beágyazza → telepítéskor a gép Pending-be enrollál, **vagy**
- **kézzel** egy gépen: `RemoteAgent.exe bootstrap "<blob>"`.

A gép **Pending**-be kerül (`autoApprove=false`) → hagyd jóvá az admin felületen → csatlakozhatsz.

---

## ✅ Ellenőrző lista

- [ ] `setup-db.sh` lefutott (DB + `db.env`)
- [ ] `schema.sql` betöltve
- [ ] `deploy-server.sh` lefutott → kulcsok generálva, service fut, agent-pubkey + CA-fingerprint feljegyezve
- [ ] `Server__PublicUrl` beállítva (env), service újraindítva
- [ ] `setup-bastion.sh`, `setup-tls.sh`, `setup-nginx.sh`, `harden.sh`
- [ ] admin temp jelszó a `journalctl`-ben
- [ ] `RemoteServer mint-blob` → minden **OK**, blob kiírva
- [ ] első gépen OpenSSH kliens (Server 2016-on kézzel) → enroll → jóváhagyás → csatlakozás
