# Comply Solutions - install an update (patch) on an existing installation.
#
# Only replaces application files. Your settings, keys, users and data are not touched.
# The API upgrades its own database tables when it starts.
#
# Usage (from the unzipped package folder, in PowerShell):
#   .\deploy-patch.ps1 -ResourceGroup rg-complysolutions -NamePrefix bankalpha
#   .\deploy-patch.ps1 -ResourceGroup rg-complysolutions -NamePrefix bankalpha -SubscriptionId <id>
#   .\deploy-patch.ps1 -ResourceGroup rg-complysolutions -NamePrefix bankalpha -Only web
#
# To roll back: run deploy-patch.ps1 from the previous package folder with the same values.

param(
    [Parameter(Mandatory = $true)][string]$ResourceGroup,
    [Parameter(Mandatory = $true)][string]$NamePrefix,
    [string]$SubscriptionId = "",
    [ValidateSet("all", "api", "web")][string]$Only = "all"
)

. (Join-Path $PSScriptRoot "lib\common.ps1")

$packageDir = $PSScriptRoot
$package = Get-PackageInfo $packageDir
$prefix = $NamePrefix.ToLowerInvariant()
$apiName = "$prefix-cs-api"
$webName = "$prefix-cs-web"

Write-Host ""
Write-Host "Comply Solutions update - version $($package.version)" -ForegroundColor White

Write-Step "Checking this computer"
Assert-AzCli
Write-Ok "Azure CLI found"

Write-Step "Verifying package files"
Test-PackageChecksums $packageDir

Write-Step "Signing in to Azure"
Connect-Azure $SubscriptionId

Write-Step "Finding your installation"
foreach ($appName in @($apiName, $webName)) {
    if (-not (Test-WebAppExists $ResourceGroup $appName)) {
        throw "App Service $appName not found in resource group $ResourceGroup. Check -ResourceGroup and -NamePrefix (they were printed at the end of install.ps1)."
    }
    Write-Ok "Found $appName"
}
$apiHost = Get-AppHost $ResourceGroup $apiName
$before = Get-ApiVersion $apiHost
Write-Ok "Installed version: $before"
Write-Ok "New version:       $($package.version)"

if ($Only -ne "web") { Publish-Api $packageDir $ResourceGroup $apiName }
if ($Only -ne "api") { Publish-Web $packageDir $ResourceGroup $webName }

$after = Get-ApiVersion $apiHost
Write-Host ""
Write-Host "Update complete." -ForegroundColor Green
Write-Host "  Version before: $before"
Write-Host "  Version now:    $after"
Write-Host "  Your app:       https://$(Get-AppHost $ResourceGroup $webName)"
Write-Host ""
Write-Host "Please open the app, sign in and check that it works (README.pdf section 7)."
