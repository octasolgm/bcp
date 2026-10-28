# Write public/assets/app-config.json before production build.
# Sources (first non-empty wins): env vars -> bcp-api appsettings.Development.json -> defaults.

param(
    [string]$OutPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "public\assets\app-config.json")
)

$webRoot = Split-Path $PSScriptRoot -Parent
$apiDevPath = Join-Path (Split-Path $webRoot -Parent) "bcp-api\appsettings.Development.json"

function Read-ApiSupabase {
    if (-not (Test-Path $apiDevPath)) { return $null }
    $dev = Get-Content $apiDevPath -Raw | ConvertFrom-Json
    return $dev.Supabase
}

$sb = Read-ApiSupabase

$cfg = [ordered]@{
    supabaseUrl     = $env:supabaseUrl
    supabaseAnonKey = $env:supabaseAnonKey
    ndApiUrl        = $env:ndApiUrl
    appUrl          = $env:appUrl
}

if (-not $cfg.supabaseUrl -and $sb) { $cfg.supabaseUrl = $sb.Url }
if (-not $cfg.supabaseAnonKey) {
    # Public anon key — same as environment.production.ts (safe to bake into static deploy)
    $cfg.supabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InByeG1rcm13cXhsbHR3am5hemF5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY5NzAwMTIsImV4cCI6MjEwMjU0NjAxMn0.nHcayH4ul9rcluW8yqzvWTEXgh-jHC6hU4WL2YauVAw"
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
