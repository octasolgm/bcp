# Build production web + optional ZIP deploy to bcp-web-dev.
# Supabase + API URLs baked at build time (environment.production.ts + app-config.json).
#
# Usage:
#   .\scripts\deploy-web.ps1
#   .\scripts\deploy-web.ps1 -ZipOnly
#   .\scripts\deploy-web.ps1 -ResourceGroup "your-rg"

param(
    [string]$AppName = "bcp-web-dev",
    [string]$ResourceGroup = "",
    [switch]$ZipOnly
)

$ErrorActionPreference = "Stop"
$scriptsDir = $PSScriptRoot
$webRoot = Split-Path $scriptsDir -Parent

function Test-AzCli { return [bool](Get-Command az -ErrorAction SilentlyContinue) }

function Resolve-ResourceGroup([string]$name, [string]$rg) {
    if ($rg) { return $rg }
    $found = az webapp list --query "[?name=='$name'].resourceGroup | [0]" -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($found)) {
        throw "Could not resolve resource group for $name. Pass -ResourceGroup or run az login."
    }
    return $found.Trim()
}

& (Join-Path $scriptsDir "deploy-prep.ps1")

$zipPath = Join-Path $webRoot "bcp-web-dist.zip"
$tempZip = Join-Path $env:TEMP "bcp-web-dist.zip"

if ($ZipOnly -or -not (Test-AzCli)) {
    if (-not (Test-AzCli)) {
        Write-Host "`nAzure CLI not found — upload zip manually:" -ForegroundColor Yellow
        Write-Host "  $zipPath"
        Write-Host "  or $tempZip"
    }
    exit 0
}

$rg = Resolve-ResourceGroup $AppName $ResourceGroup
Write-Host "`nZIP deploy -> $AppName ($rg) ..." -ForegroundColor Cyan
az webapp deploy --resource-group $rg --name $AppName --src-path $tempZip --type zip --async false
if ($LASTEXITCODE -ne 0) { throw "az webapp deploy failed" }

Write-Host "`nDone: https://$AppName.azurewebsites.net" -ForegroundColor Green
