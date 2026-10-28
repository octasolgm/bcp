# Copy one table (full data incl. JSONB) from OLD Supabase -> NEW Supabase.
#
# Usage (from bcp-api folder):
#   .\scripts\copy-supabase-table.ps1 -Table regulation_documents
#   .\scripts\copy-supabase-table.ps1 -Table regulation_points

param(
    [Parameter(Mandatory = $true)]
    [string]$Table,
    [switch]$TruncateDest,
    [string]$OldPassword = "",
    [string]$NewPassword = "",
    [string]$OldProjectRef = "hxfbzhjlmkiqhbbeftfq",
    [string]$NewProjectRef = "prxmkrmwqxlltwjnazay",
    [string]$OldPooler = "aws-1-ap-northeast-2.pooler.supabase.com",
    [string]$NewPooler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot "CopySupabaseTable\CopySupabaseTable.csproj"

if (-not $OldPassword) {
    $secure = Read-Host "Old Supabase DB password ($OldProjectRef)" -AsSecureString
    $OldPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
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
        TrustServerCertificate = "true"
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

$source = Get-NpgsqlConnString $OldProjectRef $OldPassword $OldPooler
$dest = Get-NpgsqlConnString $NewProjectRef $NewPassword $NewPooler

$dotnetArgs = @(
    "run", "--project", $project, "--no-build",
    "--", $Table, $source, $dest
)
if ($TruncateDest) { $dotnetArgs += "--truncate" }

Write-Host "=== Copy table: $Table ===" -ForegroundColor Cyan
Write-Host "OLD: $OldProjectRef @ $OldPooler"
Write-Host "NEW: $NewProjectRef"
Write-Host ""

dotnet build $project -v q | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Next: regulation_points -> analysis_runs -> analysis_points -> action_plan_history" -ForegroundColor Green
