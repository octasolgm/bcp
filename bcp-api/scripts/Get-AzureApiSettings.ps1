# Shared helper: build Azure App Service settings from local appsettings.Development.json
# Dot-source: . .\Get-AzureApiSettings.ps1

function Get-BcpApiDevJson {
    param([string]$ApiRoot = (Split-Path $PSScriptRoot -Parent))
    $devPath = Join-Path $ApiRoot "appsettings.Development.json"
    if (-not (Test-Path $devPath)) { throw "Missing $devPath" }
    return Get-Content $devPath -Raw | ConvertFrom-Json
}

function Get-BcpApiAzureSettings {
    param(
        [string]$ApiRoot = (Split-Path $PSScriptRoot -Parent),
        [string]$CorsOrigins = "http://localhost:3002,https://bcp-web-dev.azurewebsites.net"
    )
    $dev = Get-BcpApiDevJson -ApiRoot $ApiRoot
  $sb = $dev.Supabase
    $pg = $dev.ConnectionStrings.PostgreSQL

    return [ordered]@{
        ASPNETCORE_ENVIRONMENT              = "Production"
        WEBSITES_PORT                       = "8080"
        Bcp__UsePostgres                    = "true"
        Bcp__MigrateLocalDataToSupabase     = "false"
        Bcp__PostgresMaxPoolSize            = "5"
        Bcp__CorsOrigins                    = $CorsOrigins
        ConnectionStrings__PostgreSQL       = $pg
        Supabase__Url                       = $sb.Url
        Supabase__JwtSecret                 = $sb.JwtSecret
        Supabase__ServiceRoleKey            = $sb.ServiceRoleKey
        Supabase__DbHost                    = $sb.DbHost
        Supabase__DbPort                    = "$($sb.DbPort)"
        Supabase__DbUser                    = $sb.DbUser
        Supabase__DbPassword                = $sb.DbPassword
        Supabase__DbName                    = if ($sb.DbName) { $sb.DbName } else { "postgres" }
        Supabase__StorageBucket             = if ($sb.StorageBucket) { $sb.StorageBucket } else { "doc" }
        Gemini__ApiKey                      = $dev.Gemini.ApiKey
        LandingAi__ApiKey                   = $dev.LandingAi.ApiKey
    }
}

function Get-BcpApiAzureSettingsToRemove {
  # Portal overrides that block appsettings.Secrets.json in the zip
    return @(
        "ConnectionStrings__DirectUrl",
        "DATABASE_URL",
        "DIRECT_URL",
        "REGULIQ_DATABASE_URL"
    )
}
