# Export ALL data from OLD Supabase -> import into NEW Supabase
# Run from: bcp-api/scripts
#
# PREREQUISITE: Old project must NOT be "exceeding usage limits" (upgrade ~$25/mo temporarily if needed).
#
# What this migrates:
#   1. Postgres public schema (profiles, analysis_runs, documents, demo, etc.)
#   2. Auth users (auth.users + auth.identities) so logins keep working
#   3. Storage files (doc bucket PDFs) — separate step below
#
# Usage:
#   1. Install PostgreSQL client tools (psql + pg_dump) OR use Supabase Dashboard backup
#   2. Edit $Old and $New connection strings below
#   3. .\migrate-old-to-new-supabase.ps1

param(
    [switch]$ExportOnly,
    [switch]$ImportOnly,
    [string]$BackupDir = (Join-Path $PSScriptRoot "..\backups\supabase-migration")
)

$ErrorActionPreference = "Stop"

# ── OLD project (source — has all your data) ─────────────────────────────────
# Example: gmrehman project hxfbzhjlmkiqhbbeftfq
$Old = @{
    ProjectRef    = "hxfbzhjlmkiqhbbeftfq"
    DbPassword    = "YOUR_OLD_DB_PASSWORD"
    PoolerHost    = "aws-1-ap-northeast-2.pooler.supabase.com"
    Url           = "https://hxfbzhjlmkiqhbbeftfq.supabase.co"
    ServiceRoleKey = "YOUR_OLD_SERVICE_ROLE_KEY"
}

# ── NEW project (target — empty schema already created) ─────────────────────
$New = @{
    ProjectRef    = "prxmkrmwqxlltwjnazay"
    DbPassword    = "23ComplySolution@123"
    PoolerHost    = ""   # leave empty to use direct db.*.supabase.co
    Url           = "https://prxmkrmwqxlltwjnazay.supabase.co"
    ServiceRoleKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InByeG1rcm13cXhsbHR3am5hemF5Iiwicm9sZSI6InNlcnZpY2Vfcm9sZSIsImlhdCI6MTc4Njk3MDAxMiwiZXhwIjoyMTAyNTQ2MDEyfQ.iYiQLk76Cye8HjAUP-dp-0u_RdIVTZgjcaQT-92x9ww"
}

function Get-DbUri([hashtable]$cfg) {
    $pass = [uri]::EscapeDataString($cfg.DbPassword)
    $user = "postgres.$($cfg.ProjectRef)"
    if ($cfg.PoolerHost) {
        return "postgresql://${user}:${pass}@$($cfg.PoolerHost):5432/postgres"
    }
    return "postgresql://postgres:${pass}@db.$($cfg.ProjectRef).supabase.co:5432/postgres"
}

function Test-PgTools {
    $dump = Get-Command pg_dump -ErrorAction SilentlyContinue
    $psql = Get-Command psql -ErrorAction SilentlyContinue
    if (-not $dump -or -not $psql) {
        Write-Host ""
        Write-Host "pg_dump / psql not found. Use Supabase Dashboard instead:" -ForegroundColor Yellow
        Write-Host "  OLD: Database -> Backups -> Download (or SQL dump via Dashboard)"
        Write-Host "  NEW: SQL Editor -> paste data-only dump"
        Write-Host ""
        Write-Host "Or install PostgreSQL: https://www.postgresql.org/download/windows/"
        return $false
    }
    return $true
}

New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
$publicDump = Join-Path $BackupDir "01-public-data.sql"
$authDump   = Join-Path $BackupDir "02-auth-users.sql"
$storageDir = Join-Path $BackupDir "storage-doc"

$oldUri = Get-DbUri $Old
$newUri = Get-DbUri $New

Write-Host "=== BCP Supabase migration ===" -ForegroundColor Cyan
Write-Host "Backup folder: $BackupDir"
Write-Host ""

if (-not $ImportOnly) {
    if (-not (Test-PgTools)) { exit 1 }

    Write-Host "[1/3] Exporting public schema DATA from OLD project..." -ForegroundColor Green
    & pg_dump $oldUri `
        --schema=public `
        --data-only `
        --no-owner `
        --disable-triggers `
        -f $publicDump
    if ($LASTEXITCODE -ne 0) { throw "pg_dump public failed — is OLD project accessible? Upgrade billing if quota exceeded." }
    Write-Host "  -> $publicDump"

    Write-Host "[2/3] Exporting auth users from OLD project..." -ForegroundColor Green
    & pg_dump $oldUri `
        --schema=auth `
        --data-only `
        --no-owner `
        --table=auth.users `
        --table=auth.identities `
        --table=auth.sessions `
        -f $authDump 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "auth dump failed — re-create users manually in NEW project Authentication tab"
    } else {
        Write-Host "  -> $authDump"
    }

    Write-Host "[3/3] Storage export (manual / API)..." -ForegroundColor Green
    Write-Host @"

  Storage PDFs must be copied separately:
    OLD dashboard: Storage -> doc bucket -> download all files
    NEW dashboard: Storage -> doc bucket -> upload same files (keep same paths)

  Or use Supabase CLI:
    supabase storage cp -r ss://doc OLD_REF/doc $storageDir
    supabase storage cp -r $storageDir ss://doc NEW_REF/doc

"@ -ForegroundColor Yellow

    if ($ExportOnly) {
        Write-Host "Export complete. Review files in $BackupDir then run with -ImportOnly" -ForegroundColor Green
        exit 0
    }
}

if (-not $ExportOnly) {
    if (-not (Test-PgTools)) { exit 1 }

    if (-not (Test-Path $publicDump)) {
        throw "Missing $publicDump — run export first or use Dashboard backup"
    }

    Write-Host "Importing auth users into NEW project..." -ForegroundColor Green
    if (Test-Path $authDump) {
        & psql $newUri -v ON_ERROR_STOP=1 -f $authDump
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Auth import had errors — users may already exist or need manual import"
        }
    }

    Write-Host "Importing public data into NEW project..." -ForegroundColor Green
    & psql $newUri -v ON_ERROR_STOP=1 -f $publicDump
    if ($LASTEXITCODE -ne 0) { throw "Public data import failed" }

    Write-Host ""
    Write-Host "DONE. Next:" -ForegroundColor Green
    Write-Host "  1. Upload storage files to NEW doc bucket (same paths)"
    Write-Host "  2. Restart API: .\scripts\restart-api.ps1 -Detached"
    Write-Host "  3. Test login with OLD passwords (if auth import succeeded)"
}
