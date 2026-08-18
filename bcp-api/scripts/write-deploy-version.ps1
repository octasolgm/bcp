# Stamp deploy-version.json for API + web (git commit, branch, build time).
# Usage:
#   .\scripts\write-deploy-version.ps1
#   .\scripts\write-deploy-version.ps1 -Label "2026.08.18-migration-fix" -Notes "Demo list + orphan UUID fixes"

param(
    [string]$Label = "",
    [string]$Notes = "",
    [switch]$PreserveLabel
)

$ErrorActionPreference = "Stop"
$apiRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $apiRoot -Parent
$webAssets = Join-Path $repoRoot "bcp-web\public\assets\deploy-version.json"
$apiFile = Join-Path $apiRoot "deploy-version.json"

function Get-GitValue([string]$Args) {
    try {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = "SilentlyContinue"
        Push-Location $repoRoot
        $out = & git @Args 2>$null
        Pop-Location
        $ErrorActionPreference = $prev
        if ($LASTEXITCODE -ne 0 -or -not $out) { return "" }
        return ([string]$out).Trim()
    } catch {
        return ""
    }
}

$commit = Get-GitValue @("rev-parse", "--short", "HEAD")
$branch = Get-GitValue @("rev-parse", "--abbrev-ref", "HEAD")
$builtAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

$existing = $null
if (Test-Path $apiFile) {
    try {
        $existing = Get-Content $apiFile -Raw | ConvertFrom-Json
    } catch {
        $existing = $null
    }
}

if ($env:DEPLOY_VERSION_LABEL -and -not $Label.Trim()) {
    $Label = $env:DEPLOY_VERSION_LABEL
}

if (-not $Label.Trim()) {
    $datePart = (Get-Date).ToUniversalTime().ToString("yyyy.MM.dd")
    if ($commit) {
        $Label = "$datePart.$commit"
    } else {
        # Unique build number when git is unavailable (replaces old "+local" suffix).
        $Label = "$datePart.$((Get-Date).ToUniversalTime().ToString('HHmmss'))"
    }
}

if ($PreserveLabel -and $existing -and $existing.label -and $existing.label -ne "dev") {
    $Label = [string]$existing.label
    if (-not $Notes.Trim() -and $existing.notes) {
        $Notes = [string]$existing.notes
    }
}

$payload = [ordered]@{
    label   = $Label.Trim()
    api     = $Label.Trim()
    web     = $Label.Trim()
    commit  = if ($commit) { $commit } else { $null }
    branch  = if ($branch) { $branch } else { $null }
    builtAt = $builtAt
}
if ($Notes.Trim()) {
    $payload.notes = $Notes.Trim()
}

$json = ($payload | ConvertTo-Json -Depth 4 -Compress:$false)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false

foreach ($path in @($apiFile, $webAssets)) {
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($path, $json, $utf8NoBom)
}

Write-Host "Deploy version: $($Label.Trim())" -ForegroundColor Green
Write-Host "  commit: $commit"
Write-Host "  branch: $branch"
Write-Host "  api:    $apiFile"
Write-Host "  web:    $webAssets"
