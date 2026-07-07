# One-click prep before Visual Studio publish to bcp-api-dev.
# Run: .\scripts\deploy-prep.ps1

$ErrorActionPreference = "Stop"
$apiRoot = Split-Path $PSScriptRoot -Parent
Set-Location $apiRoot

Write-Host "`n[1/3] Syncing appsettings.Secrets.json from Development.json..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "sync-secrets.ps1")

Write-Host "`n[2/3] Building Release publish package..." -ForegroundColor Cyan
dotnet publish Bcp.Api.csproj -c Release -o ./publish

$secretsInPublish = Test-Path "./publish/appsettings.Secrets.json"
$devInPublish = Test-Path "./publish/appsettings.Development.json"

Write-Host "`n[3/3] Publish folder check:" -ForegroundColor Cyan
Write-Host "  appsettings.Secrets.json included: $secretsInPublish"
Write-Host "  appsettings.Development.json excluded: $(-not $devInPublish)"

if (-not $secretsInPublish) {
    Write-Error "appsettings.Secrets.json missing from publish folder. Fix sync-secrets.ps1 or appsettings.Development.json."
}

Write-Host @"

Ready to deploy.

YOUR STEPS (Visual Studio only - no Azure Portal config required for first deploy):

  1. Open Bcp.Api.csproj in Visual Studio
  2. Right-click -> Publish -> profile: bcp-api-dev - Web Deploy
  3. Click Publish

Optional later (when bcp-web is on Azure):
  Add your web URL to Bcp:CorsOrigins in appsettings.Production.json, then republish.
  Example: "http://localhost:3002,https://bcp-web-dev.azurewebsites.net"

Verify:
  https://bcp-api-dev.azurewebsites.net/dual-verify-kafka/health

"@ -ForegroundColor Yellow
