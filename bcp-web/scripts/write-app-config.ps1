# Write public/assets/app-config.json from environment variables (optional before build).
# Used for IIS/static ZIP deploy when Node server.js is not the startup command.
#
# Env vars (same names as Azure App Settings):
#   supabaseUrl, supabaseAnonKey, ndApiUrl, appUrl

param(
    [string]$OutPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "public\assets\app-config.json")
)

$cfg = [ordered]@{
    supabaseUrl     = $env:supabaseUrl
    supabaseAnonKey = $env:supabaseAnonKey
    ndApiUrl        = $env:ndApiUrl
    appUrl          = $env:appUrl
}

# Drop empty keys so the app keeps build-time defaults from environment.production.ts
$clean = [ordered]@{}
foreach ($key in $cfg.Keys) {
    $val = [string]$cfg[$key]
    if ($val.Trim()) { $clean[$key] = $val.Trim() }
}

$dir = Split-Path $OutPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$json = ($clean | ConvertTo-Json -Compress)
Set-Content -Path $OutPath -Value $json -Encoding UTF8
Write-Host "Wrote $OutPath -> $json"
