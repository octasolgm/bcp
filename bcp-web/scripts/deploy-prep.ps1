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
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "`n[3/4] Zipping dist for Azure..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $distBrowser "*") -DestinationPath $zipPath -Force

Write-Host "`n[4/4] Done." -ForegroundColor Cyan
Write-Host "  Dist folder: $distBrowser"
Write-Host "  Zip file:    $zipPath"

Write-Host @"

Deploy options:

  A) Visual Studio / Azure Portal ZIP deploy
     Upload: bcp-web-dist.zip to your bcp-web-dev App Service

  B) Deploy dist folder contents directly
     Copy everything inside dist\reguliq-web\browser\ to wwwroot
     (include web.config for SPA routing on Windows App Service)

Before deploy, confirm environment.production.ts has Supabase + API URLs,
or set Azure App Settings and use startup command: node server.js

Azure App Settings (supabaseUrl, supabaseAnonKey, ndApiUrl, appUrl) only work
at runtime when Startup Command is: node server.js
(Static IIS ZIP deploy does NOT read Application settings into the browser.)

After deploy, add CORS on API (appsettings.Production.json):
  https://bcp-web-dev.azurewebsites.net
  then republish bcp-api.

"@ -ForegroundColor Yellow
