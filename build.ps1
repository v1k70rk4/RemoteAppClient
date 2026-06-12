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

.EXAMPLE
    # Rendszergazda PowerShell-ben:
    .\build.ps1
.EXAMPLE
    .\build.ps1 -OutDir D:\kiadas
#>
[CmdletBinding()]
param(
    [string]$OutDir = 'C:\RAC\build',
    [string]$Configuration = 'Release'
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

# --- Build + másolás komponensenként ---
$failed = @()
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
