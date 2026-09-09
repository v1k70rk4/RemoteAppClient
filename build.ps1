#requires -Version 7
<#
.SYNOPSIS
    RemoteAppClient — a Windows-komponensek (agent, updater, konzol-kliens) buildelése
    egyfájlos, self-contained exékké egy kimeneti mappába (alapból C:\RAC, az install-hely).

.DESCRIPTION
    A három projekt csproj-ja már win-x64 + self-contained + single-file, így a publish
    egyetlen exét ad. Mivel a RemoteAgent és a RemoteAgent.Updater SZOLGÁLTATÁS a kimeneti
    mappából fut (zárolja a saját exéjét), a szkript rendszergazdaként LEÁLLÍTJA őket a build
    idejére, majd VISSZAINDÍTJA. A konzol-kliens (RemoteClient.exe) nem szolgáltatás.

    A végén kiírja a verziót + SHA-256-ot (ezeket a szerver feltöltőjénél / rolloutnál használod).
    A szerver (RemoteServer) NEM ide tartozik — az Linuxon fut, külön deploy-szkripttel.

.PARAMETER OutDir
    Kimeneti mappa. Alapértelmezés: C:\RAC

.PARAMETER Configuration
    Build-konfiguráció. Alapértelmezés: Release

.PARAMETER Deploy
    A build után az ÉLES telepítést is lecseréli ($InstallDir). Ilyenkor a szolgáltatások a
    másolás UTÁNIG maradnak leállítva, és a futó konzol-kliens is le lesz lőve — különben
    zárolná a saját exéjét. Rendszergazda kötelező; nélküle a szkript csak buildel.

.PARAMETER InstallDir
    Az éles telepítés helye. Alapértelmezés: C:\Program Files\RemoteAppClient

.PARAMETER NoBackup
    -Deploy mellett kihagyja a lecserélt exék mentését. Alapból mindegyik mellé kerül egy
    <név>.exe.bak (mindig a legutóbbi állapot), hogy egy rossz build kézzel visszaállítható legyen.

.EXAMPLE
    # Rendszergazda PowerShell-ben:
    .\build.ps1
.EXAMPLE
    .\build.ps1 -OutDir D:\kiadas
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'C:\RAC\build',
    [string]$Configuration = 'Release',
    [switch]$Deploy,
    [string]$InstallDir = 'C:\Program Files\RemoteAppClient',
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

# Komponens -> csproj (a kimeneti exe neve az AssemblyName: <komponens>.exe)
$components = [ordered]@{
    'RemoteAgent'         = 'src\RemoteAgent\RemoteAgent.csproj'
    'RemoteAgent.Updater' = 'src\RemoteAgent.Updater\RemoteAgent.Updater.csproj'
    'RemoteClient'        = 'src\RemoteClient\RemoteClient.csproj'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Nincs 'dotnet' a PATH-ban — telepítsd a .NET 10 SDK-t."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)

# -Deploy nélkül a szkript viselkedése változatlan. Vele viszont Program Files-ba írunk és
# szolgáltatást állítunk: mindkettő rendszergazdát kíván, és félúton elakadva rosszabb helyet
# hagynánk magunk után, mint ahonnan indultunk — ezért itt állunk meg, nem a másolásnál.
if ($Deploy) {
    if (-not $isAdmin) { throw "A -Deploy rendszergazdát igényel (Program Files + szolgáltatások)." }
    if (-not (Test-Path $InstallDir)) { throw "Nincs meg a telepítési mappa: $InstallDir" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "Kimeneti mappa: $OutDir" -ForegroundColor DarkGray
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# --- Szolgáltatások leállítása (a futó exe zárolja magát). Updatert ELŐBB (ő figyeli az agentet). ---
$toRestart = @()
if ($isAdmin) {
    foreach ($s in @('RemoteAgent.Updater', 'RemoteAgent')) {
        $svc = Get-Service $s -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            Write-Host "[svc] leállítás: $s" -ForegroundColor DarkYellow
            Stop-Service $s -Force
            try { $svc.WaitForStatus('Stopped', '00:00:20') } catch {}
            $toRestart += $s
        }
    }
}
else {
    Write-Host "FIGYELEM: nem rendszergazda — a futó RemoteAgent/Updater exéjét nem lehet cserélni." -ForegroundColor Yellow
    Write-Host "         A kliens akkor is elkészül; a teljes frissítéshez futtasd rendszergazdaként." -ForegroundColor Yellow
}

# --- A konzol-kliens kilövése: -Deploy esetén ő is cserélődik, futás közben pedig zárolja magát. ---
if ($Deploy) {
    $procs = @(Get-Process 'RemoteClient' -ErrorAction SilentlyContinue)
    foreach ($pr in $procs) {
        Write-Host "[proc] kilövés: RemoteClient (PID $($pr.Id))" -ForegroundColor DarkYellow
        try { $pr.Kill() } catch {}
    }
    foreach ($pr in $procs) { try { $pr.WaitForExit(10000) | Out-Null } catch {} }
}

# --- Build + másolás komponensenként ---
$failed = @()
$deployFailed = @()
foreach ($name in $components.Keys) {
    $proj = Join-Path $repo $components[$name]
    if (-not (Test-Path $proj)) { throw "Nincs projekt: $proj" }

    $stage = Join-Path $env:TEMP ("rac_pub_" + $name)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Host "[build] $name ..." -ForegroundColor Cyan
    dotnet publish $proj -c $Configuration -o $stage --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "A(z) $name buildje sikertelen (dotnet exit=$LASTEXITCODE)." }

    $exe = Join-Path $stage "$name.exe"
    if (-not (Test-Path $exe)) { throw "Nem készült exe: $exe" }

    try {
        Copy-Item $exe (Join-Path $OutDir "$name.exe") -Force
    }
    catch {
        $failed += $name
        Write-Warning "$name másolása nem sikerült (zárolt? fut a szolgáltatás?). Futtasd rendszergazdaként."
    }
    Remove-Item $stage -Recurse -Force
}

# --- Éles telepítés cseréje. A szolgáltatások MÉG állnak: ez a másolás egyetlen esélye arra,
#     hogy ne zárolt exébe ütközzön. Csak az cserélődik, ami ebben a futásban tényleg elkészült. ---
if ($Deploy) {
    Write-Host "[deploy] telepítés cseréje: $InstallDir" -ForegroundColor Cyan
    foreach ($name in $components.Keys) {
        if ($failed -contains $name) { Write-Warning "$name kimarad (a buildje/másolása nem sikerült)."; continue }
        $src = Join-Path $OutDir "$name.exe"
        $dst = Join-Path $InstallDir "$name.exe"
        if (-not (Test-Path $src)) { Write-Warning "$name kimarad (nincs friss exe: $src)."; continue }

        # Egyetlen, felülírt .bak: marad kézi visszaút, de nem hízik futásonként ~290 MB-tal.
        if (-not $NoBackup -and (Test-Path $dst)) { Copy-Item $dst "$dst.bak" -Force }

        try {
            Copy-Item $src $dst -Force
            if ((Get-FileHash $src -Algorithm SHA256).Hash -ne (Get-FileHash $dst -Algorithm SHA256).Hash) {
                throw "a kiírt fájl hash-e nem egyezik a forrással"
            }
            Write-Host ("  {0,-22} {1}" -f $name, (Get-Item $dst).VersionInfo.FileVersion) -ForegroundColor DarkGreen
        }
        catch {
            $deployFailed += $name
            Write-Warning "$name cseréje nem sikerült: $($_.Exception.Message)"
        }
    }
}

# --- Szolgáltatások visszaindítása (fordított sorrend: agent, majd updater) ---
[array]::Reverse($toRestart)
foreach ($s in $toRestart) {
    Write-Host "[svc] indítás: $s" -ForegroundColor DarkGreen
    Start-Service $s -ErrorAction SilentlyContinue
}

$sw.Stop()
if ($failed.Count -gt 0) {
    Write-Host "`nKész hibákkal ($([math]::Round($sw.Elapsed.TotalSeconds,1)) mp). Nem cserélt: $($failed -join ', ')" -ForegroundColor Yellow
}
else {
    Write-Host "`nKész ($([math]::Round($sw.Elapsed.TotalSeconds,1)) mp). Eredmény: $OutDir" -ForegroundColor Green
}

Get-ChildItem $OutDir -Filter *.exe | Sort-Object Name | ForEach-Object {
    $v   = (Get-Item $_.FullName).VersionInfo.FileVersion
    $sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $mb  = [math]::Round($_.Length / 1MB, 1)
    [pscustomobject]@{ Exe = $_.Name; Verzio = $v; MB = $mb; SHA256 = $sha }
} | Format-Table -AutoSize

if ($Deploy) {
    Write-Host "`nÉles telepítés ($InstallDir):" -ForegroundColor Green
    Get-ChildItem $InstallDir -Filter *.exe | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            Exe    = $_.Name
            Verzio = (Get-Item $_.FullName).VersionInfo.FileVersion
            MB     = [math]::Round($_.Length / 1MB, 1)
        }
    } | Format-Table -AutoSize

    if ($deployFailed.Count -gt 0) {
        Write-Host "Az élesben NEM cserélt: $($deployFailed -join ', ')" -ForegroundColor Yellow
    }
    Write-Host "A konzol-klienst nem indítom vissza — indítsd, amikor jónak látod." -ForegroundColor DarkGray
}
