<p align="center">
  <img src="icon/RemoteAccessBlue_256x256.png" width="128" alt="RemoteAppClient">
</p>

<h1 align="center">RemoteAppClient</h1>

<p align="center">
  Önállóan üzemeltetett <b>távelérés- és flottakezelő (RMM)</b> rendszer .NET 10-en, C2-modellben.
</p>

<p align="center">
  <a href="https://github.com/v1k70rk4/RemoteAppClient/actions/workflows/build.yml"><img src="https://github.com/v1k70rk4/RemoteAppClient/actions/workflows/build.yml/badge.svg" alt="build"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <img src="https://img.shields.io/badge/agent-Windows-0078D6?logo=windows&logoColor=white" alt="Windows">
  <img src="https://img.shields.io/badge/server-Linux-FCC624?logo=linux&logoColor=black" alt="Linux">
  <img src="https://img.shields.io/badge/verzió-1.5.0-2ea44f" alt="version 1.5.0">
  <img src="https://img.shields.io/badge/UI-MaterialSkin-7E57C2" alt="MaterialSkin">
  <a href="https://v1k70rk4.github.io/RemoteAppClient/"><img src="https://img.shields.io/badge/website-v1k70rk4.github.io-41bdf5?logo=github" alt="website"></a>
</p>

---

Windows-gépekre települő **agent** (SYSTEM service) tartja kifelé a parancscsatornát, parancsra
**reverse SSH tunnelt** épít egy bástya szerverre (ezen át megy a **VNC** távasztal), telemetriát küld,
és energiagazdálkodási / üzenet-parancsokat hajt végre. A **szerver** (Linuxon) push-olja az **aláírt**
parancsokat, kezeli az enrollmentet, a kiadási csatornákat és az MSI-gyártást. A **konzol-kliens** az
admin felület.

A képernyő-capture/encode szándékosan **nincs** itt — azt a (becsomagolt) **TightVNC** viszi. Ez a rendszer
adja a C2-logikát, a flottakezelést és a biztonsági réteget köré.

🌐 **[Website & képernyőképek](https://v1k70rk4.github.io/RemoteAppClient/)** · 🚀 **[Első indítás (FIRST-RUN.md)](FIRST-RUN.md)**

---

## Tartalom

- [Képernyőképek](#képernyőképek)
- [Mit tud](#mit-tud)
- [Architektúra](#architektúra)
- [Komponensek](#komponensek)
- [Tech stack](#tech-stack)
- [Biztonsági modell](#biztonsági-modell)
- [Repó-felépítés](#repó-felépítés)
- [Követelmények](#követelmények)
- [Gyors start](#gyors-start)
- [Kiadások](#kiadások-github-releases)
- [Verziózás](#verziózás)
- [Licenc](#licenc)

---

## Képernyőképek

> A képeket tedd a `docs/screenshots/` mappába ezeken a neveken — a README innen hivatkozza őket.

| Bejelentkezés (TOTP) | Eszközök |
|:--:|:--:|
| ![login](docs/screenshots/login.png) | ![devices](docs/screenshots/devices.png) |
| **Parancsok / Üzenetek** | **Szerver beállítások** |
| ![commands](docs/screenshots/commands.png) | ![settings](docs/screenshots/settings.png) |

---

## Mit tud

#### Flotta & távelérés
- 🖥️ **Eszközlista** — online állapot, bejelentkezett felhasználó, publikus IP, komponens-verziók; rendezés/keresés.
- 🔌 **Csatlakozás** dupla kattintással a VNC-hez a tunnelen át; jobbklikk-menü minden művelethez.
- ⚡ **Parancsok** — újraindítás (60 mp), kényszerített újraindítás, megszakítás, felhasználó kijelentkeztetés.
- 💬 **Üzenetek** — „Szabad a géped?" (igenre azonnali csatlakozás) + szöveges üzenet a gép felhasználójának.
- 📊 **Telemetria** — OS, IP/Wi-Fi/VPN, boot-idő, bejelentkezett user, komponens-verziók, agent-restartok.

#### Üzemeltetés
- 🔑 **Felhasználók** — szerepek (admin/operator), **TOTP 2FA**, jelszó-reset, **jelszó-helyreállítás** tokennel, **Windows Hello** belépés.
- 📧 **E-mail küldés** — SMTP **vagy** MS Graph (app-only); jelszó-emlékeztető, riasztások, titok-lejárat figyelő.
- 📦 **Csatornák + rollout** — `agent` / `updater` / `client` / `vnc`, `rtm` / `beta`, verzió-kapuzott rollout + promote.
- 🏗️ **MSI-gyártás** csoportonként (wixl) — agent + updater + kliens + **TightVNC** + bootstrap egy csomagban.
- 🧾 **Audit napló** minden műveletről; **branding** (tulajdonos/telefon/e-mail) a kliensben és az MSI-ben.

#### Kényelem
- 🌙 **Sötét / világos mód** (MaterialSkin témaváltó).
- 🌍 **Automatikus nyelvfelismerés** (`auto` → OS nyelve) + **egyszerű fordítás**: kulcs→érték szótár nyelvenként, így új nyelv = egy fájl.
- 🔄 **Self-update** — a Helper cseréli az agentet, az agent a Helpert; rollout exe-cserével, MSI-újratelepítés nélkül.

---

## Architektúra

```
                 ┌────────────────────────── Szerver (Linux) ──────────────────────────┐
  Agent  ──WSS───┤ /agent  (aláírt parancsok push)                                       │
 (SYSTEM)──mTLS──┤ /api/*  (telemetria, vnc-secret, enrollment)                          │
                 │ /admin/* (CSAK localhost — a kliens a gép SSH-tunneljén át éri el)    │
                 │   EF Core 9 + Pomelo → MariaDB  ·  CA + SSH-CA + parancs-aláíró kulcs │
                 └──────────────────────────────────────────────────────────────────────┘
        ssh -R (reverse tunnel)                                   ▲ kliens SSH-tunnel
  Agent ───────────────────────────►  Bástya (SSH)               │ (a gép kulcsával)
                                          ▲                    Konzol-kliens (admin gép)
   Ti / a konzol ──VNC a bástya porton────┘
```

- **Agent → szerver**: kimenő WSS (443) az `/agent`-re; aláírt parancsokat fogad, ellenőriz, végrehajt.
- **Távasztal**: parancsra `ssh -R` reverse tunnel a bástyára → a helyi TightVNC (127.0.0.1:5900) a bástya egy
  portján érhető el; a konzol oda csatlakozik a `tvnviewer.exe`-vel.
- **Admin API**: az `/admin/*` csak localhostról megy (nginx); a kliens a **helyi agent SSH-tunneljén** keresztül éri el.
  Ezért friss rendszeren az **első blobot** a szerveren kell mintázni (`mint-blob`).

---

## Komponensek

| Projekt | Mi | Hol fut |
|---|---|---|
| `RemoteAgent` | agent: C2 (WSS), telemetria, VNC-provisioning, reverse tunnel, parancsvégrehajtás, bootstrap self-enroll | kliensgépek, **SYSTEM** service |
| `RemoteAgent.Updater` | „Helper" felügyelő: watchdog + kölcsönös exe-csere (frissítés) | kliensgépek, **SYSTEM** service |
| `RemoteClient` | konzol-kliens (WinForms + MaterialSkin): admin felület | admin gép(ek) |
| `RemoteServer` | C2 hub + enrollment/CA + parancs-aláírás + csatornák/rollout + MSI-gyártó + auth + audit | szerver, **Linux** |
| `RemoteAgent.Contracts` | közös DTO-k + a kanonikus aláírás-forma (`CommandSignature`) | mindkét oldal |
| `RemoteAgent.Resources` | megosztott erőforrások | — |

A `Contracts` a kulcs: a szerver `Sign`-ol, az agent `Verify`-ol **ugyanazzal** a `CommandSignature.Canonicalize`
formával — az aláírás definíció szerint nem csúszhat szét.

---

## Tech stack

`C#` / **.NET 10** · ASP.NET Core (WSS C2) · **EF Core 9 + Pomelo → MariaDB** · WinForms + **MaterialSkin** ·
ECDSA P-256 parancs-aláírás · mTLS + per-eszköz CA · OpenSSH (reverse tunnel) · **TightVNC** (külön folyamat) ·
forrásgenerált JSON (reflection-mentes, AOT-barát) · **wixl** (MSI Linuxon).

---

## Biztonsági modell

1. **mTLS** — minden gép **saját, a szerver CA-ja által aláírt kliens-tanúsítványt** kap enrollmentkor; az agent
   pinneli a szerver TLS-certjét is.
2. **Aláírt parancsok** — ECDSA P-256 a szerver privát kulcsával; az agent a pinnelt publikus kulccsal ellenőriz.
3. **Replay-védelem** — `nonce` + `iat` minden parancson.
4. **Fix tunnel-cél** — a szerver csak a *távoli portot* adhatja meg; bástya host/user/kulcs configból, host-key pinnelt.
5. **Auth** — login + **TOTP 2FA** + session; **admin/operator** szerepek; eszköz-szintű **login-lockout**; opcionális **Windows Hello**.
6. **VNC** — loopback-only, HTTP ki, **gépenkénti** jelszó; helyben tiltható (`vnc-lock`).
7. **Enrollment** — bootstrap blob (site-token + URL); `AutoApprove=false` → **Pending**, admin hagyja jóvá.
8. **Titkok** (kulcsok, certek, jelszavak) **soha** nem kerülnek a repóba.

---

## Repó-felépítés

```
src/
  RemoteAgent/            agent (service)          RemoteServer/    szerver
  RemoteAgent.Updater/    helper (service)         RemoteClient/    konzol-kliens
  RemoteAgent.Contracts/  közös DTO + aláírás      RemoteAgent.Resources/
  RemoteServer/Data/Migrations/   schema.sql + EF baseline (Reset_1_5_0)
deploy/                   szerver setup-szkriptek (db, bastion, nginx, tls, tightvnc, harden, deploy-server)
.github/workflows/        build.yml — CI/CD (Windows + Linux build, tagre release)
build.ps1                 a 3 Windows-komponens buildje single-file exékké (alapból C:\RAC\build)
FIRST-RUN.md             nulláról induló telepítés lépésről lépésre
```

---

## Követelmények

- **Szerver**: Linux, MariaDB 10.11+, nginx (TLS + reverse proxy), SSH bástya. A `RemoteServer` self-contained.
- **Kliensgép (Windows)**: az MSI mindent feltesz (agent + updater + kliens + TightVNC). **OpenSSH kliens** kell
  (Win10/Server 2019+ tartalmazza; Server 2016-on kézzel — az agent a `System32\OpenSSH` és a `Program Files\OpenSSH` helyet is nézi).
- **Fejlesztés**: .NET 10 SDK. A `-windows` TFM-ek (agent/updater/kliens) **Windowson** épülnek; a szerver bárhol.

---

## Gyors start

**Build — Windows-komponensek** (single-file, self-contained):

```powershell
.\build.ps1                 # agent + updater + kliens -> C:\RAC\build  (verzió + SHA256 kiírva)
```

**Build — szerver**:

```bash
dotnet publish src/RemoteServer/RemoteServer.csproj -c Release -r linux-x64 --self-contained -o publish/server
```

**Telepítés nulláról** → **[FIRST-RUN.md](FIRST-RUN.md)** (a `deploy/` szkriptekkel: DB, bástya, nginx, TLS, TightVNC, hardening, szerver-deploy, majd `mint-blob`).

---

## Kiadások (GitHub Releases)

A [`build.yml`](.github/workflows/build.yml) workflow `v*` tagre **GitHub Release**-t készít. A release tartalma:

| Fájl | Mire |
|---|---|
| `RemoteAgent.exe` | feltöltöd a szerverre `agent` csomagként (`/admin/packages`) → rollout / MSI |
| `RemoteAgent.Updater.exe` | `updater` csomag |
| `RemoteClient.exe` | `client` csomag (a konzol; rolloutolható) |
| `RemoteServer-linux-x64.tar.gz` | a szerver — `/tmp/remoteserver.tar.gz`-ba kicsomagolva a `deploy/deploy-server.sh` telepíti |

Kiadás: `git tag v1.5.0 && git push origin v1.5.0` → a workflow lebuildel és feltölti az artifactokat.

---

## Verziózás

Minden komponens **1.5.0.0** (agent / helper / kliens / szerver), a `MinClientVersion` is 1.5.0.0.
Az MSI `ProductVersion`-ja a build+revision mezőt a 3. mezőbe hajtja (a Windows Installer csak az első 3 mezőt
hasonlítja), így a 4. mező léptetése is valódi in-place frissítést ad.

---

## Licenc

A repó nem tartalmaz LICENSE fájlt (privát/belső eszköz). A becsomagolt **TightVNC** a saját **GPLv2** licence
alatt áll; az agent külön folyamatként (`msiexec` / önálló exe) használja, a forrás + licenc a kiadással együtt jár.
