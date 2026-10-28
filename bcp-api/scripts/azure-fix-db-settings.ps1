# Print Azure App Service settings for bcp-api-dev after Supabase migration.
# DELETE any old settings that still reference hxfbzhjlmkiqhbbeftfq or jothhfvjkhlgotnhhlxp.
#
# Run: .\scripts\azure-fix-db-settings.ps1

$ErrorActionPreference = "Stop"
$devPath = Join-Path $PSScriptRoot "..\appsettings.Development.json"
$dev = Get-Content $devPath -Raw | ConvertFrom-Json

Write-Host "`n=== Azure bcp-api-dev — REQUIRED settings ===`n" -ForegroundColor Cyan
Write-Host "Portal: Configuration -> Application settings -> New application setting`n"

$settings = @(
    @{ Name = "ASPNETCORE_ENVIRONMENT"; Value = "Production" },
    @{ Name = "WEBSITES_PORT"; Value = "8080" },
    @{ Name = "Bcp__UsePostgres"; Value = "true" },
    @{ Name = "Bcp__PostgresMaxPoolSize"; Value = "5" },
    @{ Name = "Supabase__Url"; Value = $dev.Supabase.Url },
    @{ Name = "Supabase__JwtSecret"; Value = $dev.Supabase.JwtSecret },
    @{ Name = "Supabase__ServiceRoleKey"; Value = $dev.Supabase.ServiceRoleKey },
    @{ Name = "Supabase__DbHost"; Value = $dev.Supabase.DbHost },
    @{ Name = "Supabase__DbPort"; Value = "$($dev.Supabase.DbPort)" },
    @{ Name = "Supabase__DbUser"; Value = $dev.Supabase.DbUser },
    @{ Name = "Supabase__DbPassword"; Value = $dev.Supabase.DbPassword },
    @{ Name = "Supabase__DbName"; Value = "postgres" },
    @{ Name = "ConnectionStrings__PostgreSQL"; Value = $dev.ConnectionStrings.PostgreSQL },
    @{ Name = "Bcp__CorsOrigins"; Value = "http://localhost:3002,https://bcp-web-dev.azurewebsites.net" }
)

foreach ($s in $settings) {
    Write-Host ("{0}`n  {1}`n" -f $s.Name, $s.Value)
}

Write-Host "=== DELETE these if present (old Supabase / wrong pool) ===`n" -ForegroundColor Yellow
@(
    "Any ConnectionStrings__* with hxfbzhjlmkiqhbbeftfq",
    "Any ConnectionStrings__* with jothhfvjkhlgotnhhlxp",
    "Duplicate Supabase__* keys with old JWT secret",
    "ConnectionStrings__DirectUrl (optional — Supabase__Db* is enough)"
) | ForEach-Object { Write-Host "  - $_" }

Write-Host "`nAfter Save -> Restart bcp-api-dev`n" -ForegroundColor Green
Write-Host "Verify bootstrap ready:"
Write-Host "  https://bcp-api-dev.azurewebsites.net/health/startup"
Write-Host "  (must show status: ready, not degraded)`n"
