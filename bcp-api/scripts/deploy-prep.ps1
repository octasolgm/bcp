# One-click prep before Visual Studio publish to bcp-api-dev.
# Run: .\scripts\deploy-prep.ps1

$ErrorActionPreference = "Stop"
$apiRoot = Split-Path $PSScriptRoot -Parent
Set-Location $apiRoot

Write-Host "`n[1/4] Syncing appsettings.Secrets.json from Development.json..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "sync-secrets.ps1")

Write-Host "`n[2/4] Building Release publish package..." -ForegroundColor Cyan
if (Test-Path "./publish") {
    Remove-Item "./publish" -Recurse -Force
}
dotnet publish Bcp.Api.csproj -c Release -o ./publish

$secretsInPublish = Test-Path "./publish/appsettings.Secrets.json"
$devInPublish = Test-Path "./publish/appsettings.Development.json"

Write-Host "`n[3/4] Publish folder check:" -ForegroundColor Cyan
Write-Host "  appsettings.Secrets.json included: $secretsInPublish"
Write-Host "  appsettings.Development.json excluded: $(-not $devInPublish)"

if (-not $secretsInPublish) {
    Write-Error "appsettings.Secrets.json missing from publish folder. Fix sync-secrets.ps1 or appsettings.Development.json."
}

$zipPath = Join-Path $apiRoot "bcp-api.zip"
Write-Host "`n[4/4] Creating bcp-api.zip..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "write-deploy-zip.ps1") -SourceDir (Join-Path $apiRoot "publish") -ZipPath $zipPath

Write-Host @"

Ready to deploy.

  Recommended (settings + zip in one step):
    .\scripts\deploy-api.ps1

  Manual ZIP only:
    Upload $zipPath in Azure Portal -> Deployment Center

  Settings are synced from appsettings.Development.json -> appsettings.Secrets.json in the zip.
  deploy-api.ps1 also pushes the same values to Azure App Settings (requires az login).

Verify:
  https://bcp-api-dev.azurewebsites.net/health/startup
  https://bcp-api-dev.azurewebsites.net/dual-verify-kafka/health

"@ -ForegroundColor Yellow
