# Paste these in Azure Portal → bcp-api-dev → Configuration → Application settings
# Click "Save" then "Continue" to restart the app.

@{
    $devPath = Join-Path $PSScriptRoot "..\appsettings.Development.json"
    if (-not (Test-Path $devPath)) {
        Write-Error "Run from bcp-api/scripts — appsettings.Development.json not found at $devPath"
        exit 1
    }
    $dev = Get-Content $devPath -Raw | ConvertFrom-Json
}

Write-Host "`n=== Required Azure Application Settings ===`n" -ForegroundColor Cyan

$settings = @(
    @{ Name = "ASPNETCORE_ENVIRONMENT"; Value = "Production" },
    @{ Name = "WEBSITES_PORT"; Value = "8080" },
    @{ Name = "Bcp__UsePostgres"; Value = "true" },
    @{ Name = "Bcp__MigrateLocalDataToSupabase"; Value = "false" },
    @{ Name = "KAFKA_ENABLED"; Value = "true" },
    @{ Name = "Bcp__PostgresMaxPoolSize"; Value = "5" },
    @{ Name = "Supabase__DbPort"; Value = "5432" },
    @{ Name = "ConnectionStrings__PostgreSQL"; Value = ($dev.ConnectionStrings.PostgreSQL -replace ':6543/', ':5432/') },
    @{ Name = "Gemini__ApiKey"; Value = $dev.Gemini.ApiKey },
    @{ Name = "LandingAi__ApiKey"; Value = $dev.LandingAi.ApiKey },
    @{ Name = "Bcp__CorsOrigins"; Value = "https://YOUR-bcp-web.azurewebsites.net,http://localhost:3002" }
)

foreach ($s in $settings) {
    Write-Host ("{0}`n  → {1}`n" -f $s.Name, $s.Value)
}

Write-Host @"
Kafka connection strings are deployed via appsettings.Secrets.json (run sync-secrets.ps1 before publish).
Do NOT disable KAFKA_ENABLED unless you want local in-process queue instead of Event Hubs.

Alternative (avoids URL-encoding issues with @ in password):
  Supabase__DbPort     = 6543
  Supabase__DbHost     = $($dev.Supabase.DbHost)
  Supabase__DbUser     = $($dev.Supabase.DbUser)
  Supabase__DbPassword = $($dev.Supabase.DbPassword)
  Supabase__DbName     = postgres

After saving, verify:
  https://bcp-api-dev.azurewebsites.net/dual-verify-kafka/health

Log stream (if still 500.30):
  Azure Portal → bcp-api-dev → Monitoring → Log stream
"@ -ForegroundColor Yellow
