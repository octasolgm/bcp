# Builds appsettings.Secrets.json from appsettings.Development.json (secrets only, never committed).
# Run automatically before publish, or manually: .\scripts\sync-secrets.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$devPath = Join-Path $root "appsettings.Development.json"
$outPath = Join-Path $root "appsettings.Secrets.json"

if (-not (Test-Path $devPath)) {
    if (Test-Path $outPath) {
        Write-Host "appsettings.Secrets.json already exists - skipping sync."
        exit 0
    }
    Write-Warning "No appsettings.Development.json. Copy appsettings.Secrets.example.json to appsettings.Secrets.json and fill in values."
    exit 0
}

$dev = Get-Content $devPath -Raw | ConvertFrom-Json

$secrets = [ordered]@{
    ConnectionStrings = $dev.ConnectionStrings
    Supabase          = $dev.Supabase
    Gemini            = [ordered]@{ ApiKey = $dev.Gemini.ApiKey }
    LandingAi         = [ordered]@{ ApiKey = $dev.LandingAi.ApiKey }
}

$secrets | ConvertTo-Json -Depth 6 | Set-Content -Path $outPath -Encoding UTF8
Write-Host "Wrote $outPath (from Development.json, not committed to git)"
