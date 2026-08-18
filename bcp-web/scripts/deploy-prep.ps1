# Build production Angular app and zip dist for Azure ZIP / Web Deploy.
# Run: .\scripts\deploy-prep.ps1
# Run: .\scripts\deploy-prep.ps1 -Label "2026.08.18.2" -Notes "Nav count fixes"

param(
    [string]$Label = "",
    [string]$Notes = ""
)

$ErrorActionPreference = "Stop"
$webRoot = Split-Path $PSScriptRoot -Parent
Set-Location $webRoot

Write-Host "`n[1/5] Writing deploy version stamp..." -ForegroundColor Cyan
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$versionArgs = @{}
if ($Label.Trim()) { $versionArgs.Label = $Label.Trim() }
if ($Notes.Trim()) { $versionArgs.Notes = $Notes.Trim() }
& (Join-Path $repoRoot "bcp-api\scripts\write-deploy-version.ps1") @versionArgs
if ($Label.Trim()) { $env:DEPLOY_VERSION_LABEL = $Label.Trim() }

Write-Host "`n[2/5] Optional app-config.json from env vars..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "write-app-config.ps1")

Write-Host "`n[3/5] npm run build:prod..." -ForegroundColor Cyan
npm run build:prod

$distBrowser = Join-Path $webRoot "dist\reguliq-web\browser"
if (-not (Test-Path $distBrowser)) {
    throw "Build output not found at $distBrowser"
}

# Ensure runtime config file exists in dist (empty {} = use build defaults or Azure server.js)
$distConfig = Join-Path $distBrowser "assets\app-config.json"
if (-not (Test-Path $distConfig)) {
    $assetsDir = Split-Path $distConfig -Parent
    if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }
    Set-Content -Path $distConfig -Value "{}" -Encoding UTF8
}

$zipPath = Join-Path $webRoot "bcp-web-dist.zip"
$tempZip = Join-Path $env:TEMP "bcp-web-dist.zip"

Write-Host "`n[4/5] Zipping dist for Azure..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $zipPath
& (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $tempZip
Write-Host "  Also copied to: $tempZip (no spaces - use for Portal / VS Code deploy)"

Write-Host "`n[5/5] Done." -ForegroundColor Cyan
Write-Host "  Dist folder: $distBrowser"
Write-Host "  Zip file:    $zipPath"

Write-Host @"

Deploy options:

  Recommended:
    .\scripts\deploy-web.ps1

  Manual ZIP:
    Upload bcp-web-dist.zip (or $tempZip)

After deploy, run deploy-api.ps1 so API CORS + Supabase settings match.

"@ -ForegroundColor Yellow
