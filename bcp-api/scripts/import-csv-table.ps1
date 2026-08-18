# Import a local CSV file into NEW Supabase (full data incl. JSON columns).
# Bypasses stuck Supabase Table Editor — uses PostgreSQL COPY.
#
# Usage (from bcp-api folder):
#   .\scripts\import-csv-table.ps1 -Table analysis_runs -CsvPath "C:\Downloads\analysis_runs_rows.csv"
#   .\scripts\import-csv-table.ps1 -Table analysis_points -CsvPath "C:\Downloads\analysis_points_rows.csv" -TruncateDest

param(
    [Parameter(Mandatory = $true)]
    [string]$Table,
    [Parameter(Mandatory = $true)]
    [string]$CsvPath,
    [switch]$TruncateDest,
    [string]$NewPassword = "",
    [string]$NewProjectRef = "prxmkrmwqxlltwjnazay",
    [string]$NewPooler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot "CopySupabaseTable\CopySupabaseTable.csproj"

if (-not (Test-Path $CsvPath)) {
    Write-Error "CSV not found: $CsvPath"
}

if (-not $NewPassword) {
    $secretsPath = Join-Path $root "appsettings.Secrets.json"
    if (Test-Path $secretsPath) {
        $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
        $NewPassword = $secrets.Supabase.DbPassword
    }
    if (-not $NewPassword) {
        $secure = Read-Host "New Supabase DB password ($NewProjectRef)" -AsSecureString
        $NewPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
}

function Get-NpgsqlConnString([string]$projectRef, [string]$password, [string]$pooler) {
    $b = @{
        Database = "postgres"
        SslMode = "Require"
        Timeout = 30
        CommandTimeout = 0
    }
    if ($pooler) {
        $b.Host = $pooler
        $b.Port = 5432
        $b.Username = "postgres.$projectRef"
    }
    else {
        $b.Host = "db.$projectRef.supabase.co"
        $b.Port = 5432
        $b.Username = "postgres.$projectRef"
    }
    $b.Password = $password
    ($b.GetEnumerator() | ForEach-Object { "{0}={1}" -f $_.Key, $_.Value }) -join ";"
}

$dest = Get-NpgsqlConnString $NewProjectRef $NewPassword $NewPooler
$resolvedCsv = (Resolve-Path $CsvPath).Path

$dotnetArgs = @(
    "run", "--project", $project, "--no-build",
    "--", "import-csv", $Table, $resolvedCsv, $dest
)
if ($TruncateDest) { $dotnetArgs += "--truncate" }

Write-Host "=== Import CSV -> $Table ===" -ForegroundColor Cyan
Write-Host "File: $resolvedCsv"
Write-Host "NEW:  $NewProjectRef"
Write-Host ""

dotnet build $project -v q | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet @dotnetArgs
exit $LASTEXITCODE
