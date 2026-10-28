# Build, push Azure App Settings from local config, ZIP-deploy bcp-api-dev.
#
# Usage:
#   .\deploy-api.ps1
#   .\deploy-api.ps1 -ResourceGroup "misc-app-services-rg"
#   .\deploy-api.ps1 -ZipOnly
#   .\deploy-api.ps1 -DeployOnly -SkipBuild -ResourceGroup "misc-app-services-rg"
#   .\deploy-api.ps1 -SettingsOnly -ResourceGroup "misc-app-services-rg"

param(
    [string]$AppName = "bcp-api-dev",
    [string]$ResourceGroup = "",
    [switch]$ZipOnly,
    [switch]$DeployOnly,
    [switch]$SettingsOnly,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$scriptsDir = $PSScriptRoot
$apiRoot = Split-Path $scriptsDir -Parent
. (Join-Path $scriptsDir "Get-AzureApiSettings.ps1")

function Test-AzCli {
    return [bool](Get-Command az -ErrorAction SilentlyContinue)
}

function Resolve-ResourceGroup([string]$name, [string]$rg) {
    if ($rg) { return $rg }
    if (-not (Test-AzCli)) { return "" }
    $found = az webapp list --query "[?name=='$name'].resourceGroup | [0]" -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($found)) {
        throw "Could not resolve resource group for $name. Pass -ResourceGroup explicitly or run az login."
    }
    return $found.Trim()
}

function Push-AzureApiSettings([string]$rg, [string]$name) {
    $settings = Get-BcpApiAzureSettings -ApiRoot $apiRoot
    $toRemove = Get-BcpApiAzureSettingsToRemove

    Write-Host ""
    Write-Host "Pushing Azure App Settings for $name ..." -ForegroundColor Cyan
    foreach ($key in $toRemove) {
        az webapp config appsettings delete --resource-group $rg --name $name --setting-names $key 2>$null | Out-Null
    }

    $pairs = @()
    foreach ($entry in $settings.GetEnumerator()) {
        $escaped = [string]$entry.Value -replace '"', '\"'
        $pairs += "$($entry.Key)=$escaped"
    }
    az webapp config appsettings set --resource-group $rg --name $name --settings $pairs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "az webapp config appsettings set failed" }

    Write-Host "  Applied $($settings.Count) settings from appsettings.Development.json" -ForegroundColor Green
}

function Restart-AzureApp([string]$rg, [string]$name) {
    az webapp restart --resource-group $rg --name $name | Out-Null
    Write-Host "  Restarted $name" -ForegroundColor Green
}

function Wait-ForAppWarmup([string]$name, [int]$maxSeconds = 120) {
    $url = "https://$name.azurewebsites.net/health/startup"
    $deadline = (Get-Date).AddSeconds($maxSeconds)
    Write-Host "  Waiting for app to accept traffic ($url) ..." -ForegroundColor Cyan
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20
            if ($r.StatusCode -eq 200 -and $r.Content -match '"ready"') {
                Write-Host "  App is ready." -ForegroundColor Green
                return
            }
        } catch {
            # still starting
        }
        Start-Sleep -Seconds 8
    }
    Write-Host "  Warmup timeout - continuing anyway (deploy may still work)." -ForegroundColor Yellow
}

function Deploy-Zip([string]$rg, [string]$name, [string]$zipPath) {
    Write-Host ""
    Write-Host "ZIP deploy $zipPath -> $name ..." -ForegroundColor Cyan
    $maxAttempts = 4
    for ($i = 1; $i -le $maxAttempts; $i++) {
        az webapp deploy --resource-group $rg --name $name --src-path $zipPath --type zip --async false
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  Deploy complete." -ForegroundColor Green
            return
        }
        if ($i -lt $maxAttempts) {
            $wait = 20 * $i
            Write-Host "  Deploy failed (attempt $i/$maxAttempts). Waiting ${wait}s (app may be restarting) ..." -ForegroundColor Yellow
            Start-Sleep -Seconds $wait
            Wait-ForAppWarmup $name 60
        }
    }
    throw "az webapp deploy failed after $maxAttempts attempts"
}

function Show-NoAzCliHelp([string]$zipPath) {
    Write-Host ""
    Write-Host "Azure CLI (az) is not installed." -ForegroundColor Yellow
    Write-Host "  winget install -e --id Microsoft.AzureCLI"
    Write-Host "  https://aka.ms/installazurecliwindows"
    Write-Host ""
    Write-Host "Manual: upload $zipPath in Portal Deployment Center"
    Write-Host "        then .\azure-fix-db-settings.ps1"
}

# --- Build ---
if (-not $SkipBuild -and -not $SettingsOnly -and -not $DeployOnly) {
    & (Join-Path $scriptsDir "deploy-prep.ps1")
}

$zipPath = Join-Path $apiRoot "bcp-api.zip"

if ($ZipOnly) {
    Write-Host ""
    Write-Host "ZipOnly - upload manually: $zipPath" -ForegroundColor Yellow
    Show-NoAzCliHelp $zipPath
    exit 0
}

if (-not (Test-AzCli)) {
    Show-NoAzCliHelp $zipPath
    exit 0
}

$rg = Resolve-ResourceGroup $AppName $ResourceGroup
Write-Host "Target: $AppName (resource group: $rg)" -ForegroundColor Cyan

if (-not $DeployOnly) {
    Push-AzureApiSettings $rg $AppName
}

if (-not $SettingsOnly) {
    if (-not (Test-Path $zipPath)) {
        throw "Missing $zipPath - run without -DeployOnly/-SettingsOnly first"
    }
    Deploy-Zip $rg $AppName $zipPath
}

if (-not $DeployOnly) {
    Restart-AzureApp $rg $AppName
    Wait-ForAppWarmup $AppName 120
}

Write-Host ""
Write-Host "Done. Verify:" -ForegroundColor Green
Write-Host "  https://$AppName.azurewebsites.net/health/startup"
Write-Host "  https://$AppName.azurewebsites.net/dual-verify-kafka/health"
