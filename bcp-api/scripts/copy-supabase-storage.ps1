# Copy PDF/Word files from OLD Supabase Storage bucket -> NEW (same object paths).
# DB rows (stored_documents.storage_path) are already imported — paths must match exactly.
#
# Prerequisite: OLD project must allow Storage API access (not quota-blocked).
#
# Step 1 — export paths from NEW Supabase SQL Editor:
#   SELECT storage_path FROM stored_documents WHERE storage_path <> ''
#   UNION
#   SELECT source_storage_path FROM stored_documents WHERE source_storage_path IS NOT NULL;
#   (Download CSV or copy column -> save as paths.txt, one path per line)
#
# Step 2 — run:
#   .\scripts\copy-supabase-storage.ps1 -FromDatabase
#   .\scripts\copy-supabase-storage.ps1 -PathsFile "C:\Users\Pc\Downloads\paths.txt"
#   .\scripts\copy-supabase-storage.ps1 -FromDatabase -DryRun
#
# Or copy entire bucket with Supabase CLI (if installed + logged in):
#   supabase storage cp -r ss://doc/hxfbzhjlmkiqhbbeftfq ./storage-doc-backup
#   supabase storage cp -r ./storage-doc-backup ss://doc/prxmkrmwqxlltwjnazay

param(
    [string]$PathsFile = "",
    [switch]$FromDatabase,
    [switch]$DryRun,
    [string]$Bucket = "doc",
    [string]$OldUrl = "https://hxfbzhjlmkiqhbbeftfq.supabase.co",
    [string]$NewUrl = "https://prxmkrmwqxlltwjnazay.supabase.co",
    [string]$OldServiceRoleKey = "",
    [string]$NewServiceRoleKey = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Read-Secret([string]$name) {
    $secretsPath = Join-Path $root "appsettings.Secrets.json"
    if (-not (Test-Path $secretsPath)) { return $null }
    $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
    return $secrets.Supabase.$name
}

function Get-AuthHeaders([string]$serviceRoleKey) {
    @{
        apikey        = $serviceRoleKey
        Authorization = "Bearer $serviceRoleKey"
    }
}

function Test-BucketExists([string]$baseUrl, [hashtable]$headers, [string]$bucket) {
    try {
        $null = Invoke-RestMethod -Uri "$baseUrl/storage/v1/bucket/$bucket" -Headers $headers -Method GET
        return $true
    } catch {
        return $false
    }
}

function Copy-Object(
    [string]$path,
    [string]$oldBase,
    [hashtable]$oldHeaders,
    [string]$newBase,
    [hashtable]$newHeaders,
    [string]$bucket
) {
    $encoded = [uri]::EscapeUriString($path.Trim().TrimStart('/'))
    $downloadUrl = "$oldBase/storage/v1/object/$bucket/$encoded"
    $uploadUrl = "$newBase/storage/v1/object/$bucket/$encoded"

    if ($DryRun) {
        Write-Host "  [dry-run] $path"
        return
    }

    $resp = Invoke-WebRequest -Uri $downloadUrl -Headers $oldHeaders -Method GET -UseBasicParsing
    $contentType = $resp.Headers["Content-Type"]
    if (-not $contentType) { $contentType = "application/octet-stream" }

    $uploadHeaders = $newHeaders.Clone()
    $uploadHeaders["x-upsert"] = "true"

    $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method POST `
        -Body $resp.Content -ContentType $contentType -UseBasicParsing
    $kb = [math]::Round($resp.RawContentLength / 1KB, 1)
    Write-Host "  OK  $path ($kb KB)"
}

function Get-StoragePathsFromDatabase() {
    $secretsPath = Join-Path $root "appsettings.Secrets.json"
    if (-not (Test-Path $secretsPath)) { throw "Missing appsettings.Secrets.json" }
    $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
    $password = $secrets.Supabase.DbPassword
    $user = if ($secrets.Supabase.DbUser) { $secrets.Supabase.DbUser } else { "postgres.prxmkrmwqxlltwjnazay" }
    $dest = "Host=db.prxmkrmwqxlltwjnazay.supabase.co;Port=5432;Database=postgres;Username=$user;Password=$password;Ssl Mode=Require"
    if ($secrets.ConnectionStrings.PostgreSQL) {
        $dest = $secrets.ConnectionStrings.PostgreSQL
    }
    $project = Join-Path $PSScriptRoot "CopySupabaseTable\CopySupabaseTable.csproj"
    dotnet build $project -v q | Out-Null
    $lines = dotnet run --project $project --no-build -- export-storage-paths $dest 2>$null
    return $lines | Where-Object { $_ -and $_ -notmatch '^(Direct connection|Destination pooler|Wrote )' }
}

if (-not $PathsFile -and -not $FromDatabase) {
    throw "Provide -PathsFile <file> or -FromDatabase to load paths from stored_documents."
}

if ($PathsFile -and -not (Test-Path $PathsFile) -and -not $FromDatabase) {
    throw "Paths file not found: $PathsFile"
}

if (-not $NewServiceRoleKey) { $NewServiceRoleKey = Read-Secret "ServiceRoleKey" }
if (-not $OldServiceRoleKey) {
    $secure = Read-Host "OLD Supabase service_role key ($(([uri]$OldUrl).Host))" -AsSecureString
    $OldServiceRoleKey = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
if (-not $NewServiceRoleKey) {
    $secure = Read-Host "NEW Supabase service_role key ($(([uri]$NewUrl).Host))" -AsSecureString
    $NewServiceRoleKey = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

$oldBase = $OldUrl.Trim().TrimEnd('/')
$newBase = $NewUrl.Trim().TrimEnd('/')
$oldHeaders = Get-AuthHeaders $OldServiceRoleKey
$newHeaders = Get-AuthHeaders $NewServiceRoleKey

Write-Host "=== Copy Supabase Storage ($Bucket) ===" -ForegroundColor Cyan
Write-Host "OLD: $oldBase"
Write-Host "NEW: $newBase"
if ($DryRun) { Write-Host "DRY RUN — no files copied" -ForegroundColor Yellow }

if (-not (Test-BucketExists $newBase $newHeaders $Bucket)) {
    throw @"
NEW project is missing private bucket '$Bucket'.
Create it: Supabase Dashboard -> Storage -> New bucket -> name 'doc' -> Private -> Create
"@
}

$paths = if ($FromDatabase) {
    Write-Host "Loading storage paths from stored_documents ..." -ForegroundColor Cyan
    @(Get-StoragePathsFromDatabase)
} else {
    Get-Content $PathsFile |
        ForEach-Object { $_.Trim().Trim('"') } |
        Where-Object { $_ -and $_ -notmatch '^(storage_path|path)$' } |
        Select-Object -Unique
}

if ($paths.Count -eq 0) { throw "No storage paths in $PathsFile" }

Write-Host ""
Write-Host "Copying $($paths.Count) object(s)..." -ForegroundColor Green
$ok = 0
$fail = 0
foreach ($p in $paths) {
    try {
        Copy-Object $p $oldBase $oldHeaders $newBase $newHeaders $Bucket
        $ok++
    } catch {
        Write-Host "  FAIL $p — $($_.Exception.Message)" -ForegroundColor Red
        $fail++
    }
}

Write-Host ""
Write-Host "Done: $ok copied, $fail failed." -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Yellow" })
if ($fail -gt 0) {
    Write-Host "If OLD project is quota-blocked, upgrade billing or wait for reset, then re-run." -ForegroundColor Yellow
}
Write-Host "Test: Regulation Documents -> Download / View PDF in the app."
