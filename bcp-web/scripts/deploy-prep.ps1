# Build production Angular app and zip dist for Azure ZIP / Web Deploy.
# Run: .\scripts\deploy-prep.ps1

$ErrorActionPreference = "Stop"
$webRoot = Split-Path $PSScriptRoot -Parent
Set-Location $webRoot

Write-Host "`n[1/4] Optional app-config.json from env vars..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "write-app-config.ps1")

Write-Host "`n[2/4] npm run build:prod..." -ForegroundColor Cyan
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

Write-Host "`n[3/4] Zipping dist for Azure..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $zipPath
& (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $tempZip
Write-Host "  Also copied to: $tempZip (no spaces - use for Portal / VS Code deploy)"

Write-Host "`n[4/4] Done." -ForegroundColor Cyan
Write-Host "  Dist folder: $distBrowser"
Write-Host "  Zip file:    $zipPath"

Write-Host @"

Deploy options:

  A) VS Code Azure: Deploy to Web App on the ZIP file (not the browser folder)
     $tempZip

  B) Azure Portal ZIP deploy - upload bcp-web-dist.zip

  C) Do NOT deploy dist\browser folder when repo path has a space (bcp new).

After deploy, add CORS on API (appsettings.Production.json):
  https://bcp-web-dev.azurewebsites.net
  then republish bcp-api.

"@ -ForegroundColor Yellow
