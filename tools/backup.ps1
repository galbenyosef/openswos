# tools/backup.ps1 — timestamped 7z snapshot of the working tree.
#
# Usage:
#   .\tools\backup.ps1                # → .backups/YYYYMMDD_HHMMSS_snapshot.7z
#   .\tools\backup.ps1 -label myname  # → .backups/YYYYMMDD_HHMMSS_myname.7z
#
# Excludes everything that's gitignored (originals, build outputs, external repos,
# extracted assets, godot/dotnet caches). Uses 'normal' compression (-mx5).

param(
    [string]$label = "snapshot"
)

$ErrorActionPreference = 'Stop'

# Repo root = parent of tools/
$repo = Split-Path -Parent $PSScriptRoot
$backupDir = Join-Path $repo ".backups"
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$archive = Join-Path $backupDir "${timestamp}_${label}.7z"

# Locate 7z. Fall back to PATH if the standard install path isn't there.
$sevenZip = "C:\Program Files\7-Zip\7z.exe"
if (-not (Test-Path $sevenZip)) {
    $cmd = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($null -eq $cmd) { throw "7z.exe not found. Install 7-Zip or set $sevenZip." }
    $sevenZip = $cmd.Source
}

# Exclusions — keep in sync with .gitignore.
$excludes = @(
    "-xr!Swos9697_Amiga"
    "-xr!Swos9697_PC"
    "-xr!assets\extracted"
    "-xr!build"
    "-xr!export"
    "-xr!dist"
    "-xr!.tools"
    "-xr!external"
    "-xr!.godot"
    "-xr!.import"
    "-xr!bin"
    "-xr!obj"
    "-xr!.vs"
    "-xr!.vscode"
    "-xr!.idea"
    "-xr!.backups"
    "-xr!Thumbs.db"
    "-xr!.DS_Store"
)

Write-Output "Creating $archive ..."
Push-Location $repo
try {
    & $sevenZip a -mx5 -bsp1 -bso0 $archive '.' @excludes
    if ($LASTEXITCODE -ne 0) { throw "7z exited with code $LASTEXITCODE" }
} finally {
    Pop-Location
}

$sizeMB = [math]::Round((Get-Item $archive).Length / 1MB, 2)
Write-Output ""
Write-Output "Backup created: $archive ($sizeMB MB)"
