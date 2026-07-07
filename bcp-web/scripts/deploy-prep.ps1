# Build production Angular app and zip dist for Azure ZIP / Web Deploy.
# Run: .\scripts\deploy-prep.ps1

$ErrorActionPreference = "Stop"
$webRoot = Split-Path $PSScriptRoot -Parent
Set-Location $webRoot

Write-Host "`n[1/3] npm run build:prod..." -ForegroundColor Cyan
npm run build:prod

$distBrowser = Join-Path $webRoot "dist\reguliq-web\browser"
if (-not (Test-Path $distBrowser)) {
    throw "Build output not found at $distBrowser"
}

$zipPath = Join-Path $webRoot "bcp-web-dist.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "`n[2/3] Zipping dist for Azure..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $distBrowser "*") -DestinationPath $zipPath -Force

Write-Host "`n[3/3] Done." -ForegroundColor Cyan
Write-Host "  Dist folder: $distBrowser"
Write-Host "  Zip file:    $zipPath"

Write-Host @"

Deploy options:

  A) Visual Studio / Azure Portal ZIP deploy
     Upload: bcp-web-dist.zip to your bcp-web-dev App Service

  B) Deploy dist folder contents directly
     Copy everything inside dist\reguliq-web\browser\ to wwwroot
     (include web.config for SPA routing on Windows App Service)

Before deploy, confirm environment.production.ts points at:
  https://bcp-api-dev.azurewebsites.net

After deploy, add CORS on API (appsettings.Production.json):
  https://bcp-web-dev.azurewebsites.net
  then republish bcp-api.

"@ -ForegroundColor Yellow
