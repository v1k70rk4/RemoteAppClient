#requires -Version 7
<#
.SYNOPSIS
    RemoteAppClient — builds the Windows components (agent, updater, operator console) into
    single-file, self-contained exes in one output folder (C:\RAC by default, the install location).

.DESCRIPTION
    All three csproj files are already win-x64 + self-contained + single-file, so publish yields a
    single exe each. Because RemoteAgent and RemoteAgent.Updater are SERVICES running from the output
    folder (they lock their own exe), this script STOPS them for the duration of the build when run as
    administrator, then STARTS them again. The console client (RemoteClient.exe) is not a service.

    At the end it prints version + SHA-256 for each exe (what you feed to the server's package upload
    and rollout). The server (RemoteServer) does NOT belong here — it runs on Linux with its own deploy.

.PARAMETER OutDir
    Output folder. Default: C:\RAC\build

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER Deploy
    After building, also replace the LIVE installation in $InstallDir. The services then stay stopped
    UNTIL the copy is done, and the running console client is killed as well — otherwise it would hold
    a lock on its own exe. Requires administrator; without this switch the script only builds.

.PARAMETER InstallDir
    Location of the live installation. Default: C:\Program Files\RemoteAppClient

.PARAMETER NoBackup
    With -Deploy, skip saving the replaced exes. By default each one gets a <name>.exe.bak beside it
    (always the previous state), so a bad build can be rolled back by hand.

.EXAMPLE
    # In an administrator PowerShell:
    .\build.ps1
.EXAMPLE
    .\build.ps1 -OutDir D:\release
.EXAMPLE
    # Build and replace the live installation in one go:
    .\build.ps1 -Deploy
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

# Component -> csproj (the output exe is named after AssemblyName: <component>.exe)
$components = [ordered]@{
    'RemoteAgent'         = 'src\RemoteAgent\RemoteAgent.csproj'
    'RemoteAgent.Updater' = 'src\RemoteAgent.Updater\RemoteAgent.Updater.csproj'
    'RemoteClient'        = 'src\RemoteClient\RemoteClient.csproj'
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "No 'dotnet' on PATH — install the .NET 10 SDK."
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)

# Without -Deploy the script behaves exactly as before. With it we write into Program Files and stop
# services: both need administrator, and failing halfway would leave things in a worse state than we
# started from — so we stop here, not at the copy.
if ($Deploy) {
    if (-not $isAdmin) { throw "-Deploy requires administrator (Program Files + services)." }
    if (-not (Test-Path $InstallDir)) { throw "Install folder not found: $InstallDir" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Write-Host "Output folder: $OutDir" -ForegroundColor DarkGray
$sw = [System.Diagnostics.Stopwatch]::StartNew()

# --- Stop the services (a running exe locks itself). Updater FIRST — it supervises the agent. ---
$toRestart = @()
if ($isAdmin) {
    foreach ($s in @('RemoteAgent.Updater', 'RemoteAgent')) {
        $svc = Get-Service $s -ErrorAction SilentlyContinue
        if ($svc -and $svc.Status -eq 'Running') {
            Write-Host "[svc] stopping: $s" -ForegroundColor DarkYellow
            Stop-Service $s -Force
            try { $svc.WaitForStatus('Stopped', '00:00:20') } catch {}
            $toRestart += $s
        }
    }
}
else {
    Write-Host "WARNING: not administrator — the running RemoteAgent/Updater exe cannot be replaced." -ForegroundColor Yellow
    Write-Host "         The client is still built; run as administrator for a full update." -ForegroundColor Yellow
}

# --- Kill the console client: with -Deploy it is replaced too, and it locks itself while running. ---
if ($Deploy) {
    $procs = @(Get-Process 'RemoteClient' -ErrorAction SilentlyContinue)
    foreach ($pr in $procs) {
        Write-Host "[proc] killing: RemoteClient (PID $($pr.Id))" -ForegroundColor DarkYellow
        try { $pr.Kill() } catch {}
    }
    foreach ($pr in $procs) { try { $pr.WaitForExit(10000) | Out-Null } catch {} }
}

# --- Build + copy, per component ---
$failed = @()
$deployFailed = @()
foreach ($name in $components.Keys) {
    $proj = Join-Path $repo $components[$name]
    if (-not (Test-Path $proj)) { throw "Project not found: $proj" }

    $stage = Join-Path $env:TEMP ("rac_pub_" + $name)
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Write-Host "[build] $name ..." -ForegroundColor Cyan
    dotnet publish $proj -c $Configuration -o $stage --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build of $name failed (dotnet exit=$LASTEXITCODE)." }

    $exe = Join-Path $stage "$name.exe"
    if (-not (Test-Path $exe)) { throw "No exe produced: $exe" }

    try {
        Copy-Item $exe (Join-Path $OutDir "$name.exe") -Force
    }
    catch {
        $failed += $name
        Write-Warning "Copying $name failed (locked? service running?). Run as administrator."
    }
    Remove-Item $stage -Recurse -Force
}

# --- Replace the live installation. The services are STILL stopped: this is the one window in which
#     the copy will not hit a locked exe. Only what this run actually produced gets replaced. ---
if ($Deploy) {
    Write-Host "[deploy] replacing installation: $InstallDir" -ForegroundColor Cyan
    foreach ($name in $components.Keys) {
        if ($failed -contains $name) { Write-Warning "$name skipped (its build/copy failed)."; continue }
        $src = Join-Path $OutDir "$name.exe"
        $dst = Join-Path $InstallDir "$name.exe"
        if (-not (Test-Path $src)) { Write-Warning "$name skipped (no fresh exe: $src)."; continue }

        # A single, overwritten .bak: a manual way back stays available without growing ~290 MB per run.
        if (-not $NoBackup -and (Test-Path $dst)) { Copy-Item $dst "$dst.bak" -Force }

        try {
            Copy-Item $src $dst -Force
            if ((Get-FileHash $src -Algorithm SHA256).Hash -ne (Get-FileHash $dst -Algorithm SHA256).Hash) {
                throw "the written file's hash does not match the source"
            }
            Write-Host ("  {0,-22} {1}" -f $name, (Get-Item $dst).VersionInfo.FileVersion) -ForegroundColor DarkGreen
        }
        catch {
            $deployFailed += $name
            Write-Warning "Replacing $name failed: $($_.Exception.Message)"
        }
    }
}

# --- Start the services again (reverse order: agent first, then updater) ---
[array]::Reverse($toRestart)
foreach ($s in $toRestart) {
    Write-Host "[svc] starting: $s" -ForegroundColor DarkGreen
    Start-Service $s -ErrorAction SilentlyContinue
}

$sw.Stop()
if ($failed.Count -gt 0) {
    Write-Host "`nDone with errors ($([math]::Round($sw.Elapsed.TotalSeconds,1))s). Not replaced: $($failed -join ', ')" -ForegroundColor Yellow
}
else {
    Write-Host "`nDone ($([math]::Round($sw.Elapsed.TotalSeconds,1))s). Output: $OutDir" -ForegroundColor Green
}

Get-ChildItem $OutDir -Filter *.exe | Sort-Object Name | ForEach-Object {
    $v   = (Get-Item $_.FullName).VersionInfo.FileVersion
    $sha = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    $mb  = [math]::Round($_.Length / 1MB, 1)
    [pscustomobject]@{ Exe = $_.Name; Version = $v; MB = $mb; SHA256 = $sha }
} | Format-Table -AutoSize

if ($Deploy) {
    Write-Host "`nLive installation ($InstallDir):" -ForegroundColor Green
    Get-ChildItem $InstallDir -Filter *.exe | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            Exe     = $_.Name
            Version = (Get-Item $_.FullName).VersionInfo.FileVersion
            MB      = [math]::Round($_.Length / 1MB, 1)
        }
    } | Format-Table -AutoSize

    if ($deployFailed.Count -gt 0) {
        Write-Host "NOT replaced in the live installation: $($deployFailed -join ', ')" -ForegroundColor Yellow
    }
    Write-Host "The console client is not restarted — start it when you are ready." -ForegroundColor DarkGray
}
