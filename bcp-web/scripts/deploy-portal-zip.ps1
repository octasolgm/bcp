# Deploy bcp-web to Azure without VS Code / Visual Studio (no corrupt zip, no publish password).
# Run: .\scripts\deploy-portal-zip.ps1

$ErrorActionPreference = "Stop"
$webRoot = Split-Path $PSScriptRoot -Parent
$distBrowser = Join-Path $webRoot "dist\reguliq-web\browser"
$zipRepo = Join-Path $webRoot "bcp-web-dist.zip"
$zipTemp = Join-Path $env:TEMP "bcp-web-dist.zip"

if (-not (Test-Path $distBrowser)) {
    Write-Host "dist not found - running full build..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "deploy-prep.ps1")
} else {
    & (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $zipRepo
    & (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir $distBrowser -ZipPath $zipTemp
}

Write-Host @"

=== bcp-web Portal ZIP deploy ===

Do NOT deploy the folder:
  dist\reguliq-web\browser
(VS Code zips it badly when the repo path has a space: bcp new)

Upload ONE of these zip files:

  $zipTemp
  $zipRepo

VS Code Azure: right-click the zip file -> Deploy to Web App -> bcp-web-dev

"@ -ForegroundColor Yellow

if (Test-Path $zipTemp) {
    explorer.exe "/select,$zipTemp"
}
