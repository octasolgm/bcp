# Write public/assets/app-config.json before production build.
# Sources (first non-empty wins): env vars -> bcp-api appsettings.Development.json -> defaults.

param(
    [string]$OutPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "public\assets\app-config.json")
)

$webRoot = Split-Path $PSScriptRoot -Parent
$apiDevPath = Join-Path (Split-Path $webRoot -Parent) "bcp-api\appsettings.Development.json"

# Sign-in runs through our own API, so the web app carries no identity provider URL or key.

$cfg = [ordered]@{
    ndApiUrl        = $env:ndApiUrl
    appUrl          = $env:appUrl
}

if (-not $cfg.ndApiUrl) { $cfg.ndApiUrl = "https://bcp-api-dev.azurewebsites.net" }
if (-not $cfg.appUrl) { $cfg.appUrl = "https://bcp-web-dev.azurewebsites.net" }

$clean = [ordered]@{}
foreach ($key in $cfg.Keys) {
    $val = [string]$cfg[$key]
    if ($val.Trim()) { $clean[$key] = $val.Trim() }
}

$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$json = ($clean | ConvertTo-Json -Compress)
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutPath, $json, $utf8NoBom)
Write-Host "Wrote $OutPath -> $json"
