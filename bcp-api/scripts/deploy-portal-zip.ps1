# Deploy bcp-api to Azure via Portal ZIP (no Visual Studio publish password).
# Run: .\scripts\deploy-portal-zip.ps1

$ErrorActionPreference = "Stop"
$apiRoot = Split-Path $PSScriptRoot -Parent
$zipPath = Join-Path $apiRoot "bcp-api.zip"

if (-not (Test-Path $zipPath)) {
    Write-Host "bcp-api.zip missing — running deploy-prep..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "deploy-prep.ps1")
}

Write-Host @"

=== bcp-api Portal ZIP deploy (skip VS password prompt) ===

Visual Studio Publish asks for a password = Azure *deployment* credentials
(not Supabase, not DB). You can skip VS entirely:

Upload this zip in Azure Portal:

  $zipPath

Steps:
  1. Azure Portal -> bcp-api-dev -> Deployment Center
  2. ZIP Deploy
  3. Upload bcp-api.zip
  4. Restart the app

If you still want Visual Studio Publish (Web Deploy profile):
  Portal -> bcp-api-dev -> Deployment Center -> FTPS credentials
  Username: `$bcp-api-dev
  Password: copy from that page (Reset if unknown)
  Paste in VS Publish dialog and tick Save password.

"@ -ForegroundColor Yellow

if (Test-Path $zipPath) {
    explorer.exe "/select,$zipPath"
}
