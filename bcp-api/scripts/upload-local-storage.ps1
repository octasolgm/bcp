# Upload local PDF/DOCX files to NEW Supabase Storage (doc bucket).
# Use when OLD project is quota-blocked and copy-supabase-storage.ps1 cannot download.
#
# Matches files by filename (last path segment) against storage_paths from DB.
#
# Usage:
#   .\scripts\upload-local-storage.ps1 -LocalDirs "C:\Users\Pc\Downloads\bundle","C:\Users\Pc\Downloads"
#   .\scripts\upload-local-storage.ps1 -LocalDirs "C:\path\to\files" -DryRun

param(
    [string[]]$LocalDirs = @("C:\Users\Pc\Downloads\bundle", "C:\Users\Pc\Downloads"),
    [switch]$FromDatabase = $true,
    [string]$PathsFile = "",
    [switch]$DryRun,
    [string]$Bucket = "doc",
    [string]$NewUrl = "https://prxmkrmwqxlltwjnazay.supabase.co",
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

function Get-StoragePathsFromDatabase() {
    $secretsPath = Join-Path $root "appsettings.Secrets.json"
    $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
    $dest = $secrets.ConnectionStrings.PostgreSQL
    $project = Join-Path $PSScriptRoot "CopySupabaseTable\CopySupabaseTable.csproj"
    dotnet build $project -v q | Out-Null
    $lines = dotnet run --project $project --no-build -- export-storage-paths $dest 2>$null
    return @($lines | Where-Object { $_ -and $_ -notmatch '^(Direct connection|Destination pooler|Wrote )' })
}

function Find-LocalFile([string]$storagePath, [hashtable]$index) {
    $name = [System.IO.Path]::GetFileName($storagePath)
    if ($index.ContainsKey($name)) { return $index[$name] }

    $aliases = @{
        "I M P T F S.pdf" = "I M P T F S.pdf.pdf"
        "Internal A M L M a n u a l 290626 (1).pdf" = "Internal A M L M a n u a l 290626.pdf"
        "amlcft cb uae decision.pdf" = "A N C TI O N E.pdf.pdf"
    }
    if ($aliases.ContainsKey($name) -and $index.ContainsKey($aliases[$name])) {
        return $index[$aliases[$name]]
    }

    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    foreach ($key in $index.Keys) {
        $keyBase = [System.IO.Path]::GetFileNameWithoutExtension($key)
        if ($keyBase -eq $base) { return $index[$key] }
        if ($keyBase.StartsWith($base) -or $base.StartsWith($keyBase)) { return $index[$key] }
    }
    return $null
}

if (-not $NewServiceRoleKey) { $NewServiceRoleKey = Read-Secret "ServiceRoleKey" }
if (-not $NewServiceRoleKey) {
    $secure = Read-Host "NEW Supabase service_role key" -AsSecureString
    $NewServiceRoleKey = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

$paths = if ($FromDatabase) {
    Get-StoragePathsFromDatabase
} elseif ($PathsFile) {
    Get-Content $PathsFile | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ }
} else {
    throw "Provide -FromDatabase or -PathsFile"
}

$index = @{}
foreach ($dir in $LocalDirs) {
    if (-not (Test-Path $dir)) {
        Write-Host "Skip missing folder: $dir" -ForegroundColor Yellow
        continue
    }
    Get-ChildItem $dir -File -Recurse -Include *.pdf,*.docx,*.doc | ForEach-Object {
        if (-not $index.ContainsKey($_.Name)) {
            $index[$_.Name] = $_.FullName
        }
    }
}

$newBase = $NewUrl.Trim().TrimEnd('/')
$newHeaders = Get-AuthHeaders $NewServiceRoleKey

Write-Host "=== Upload local files -> NEW Storage ($Bucket) ===" -ForegroundColor Cyan
Write-Host "NEW: $newBase"
Write-Host "Local search dirs: $($LocalDirs -join ', ')"
Write-Host "Storage paths to fill: $($paths.Count)"
if ($DryRun) { Write-Host "DRY RUN" -ForegroundColor Yellow }
Write-Host ""

$ok = 0
$missing = 0
$fail = 0

foreach ($storagePath in $paths) {
    $local = Find-LocalFile $storagePath $index
    if (-not $local) {
        Write-Host "  MISS  $storagePath" -ForegroundColor Yellow
        $fileName = [System.IO.Path]::GetFileName($storagePath)
        Write-Host "        (no local file named $fileName)" -ForegroundColor DarkYellow
        $missing++
        continue
    }

    if ($DryRun) {
        Write-Host "  [dry-run] $storagePath <- $local"
        $ok++
        continue
    }

    try {
        $encoded = [uri]::EscapeUriString($storagePath.Trim().TrimStart('/'))
        $uploadUrl = "$newBase/storage/v1/object/$Bucket/$encoded"
        $bytes = [System.IO.File]::ReadAllBytes($local)
        $ext = [System.IO.Path]::GetExtension($local).ToLowerInvariant()
        $contentType = switch ($ext) {
            ".pdf" { "application/pdf" }
            ".docx" { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }
            ".doc" { "application/msword" }
            default { "application/octet-stream" }
        }

        $uploadHeaders = $newHeaders.Clone()
        $uploadHeaders["x-upsert"] = "true"

        $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method POST `
            -Body $bytes -ContentType $contentType -UseBasicParsing
        $kb = [math]::Round($bytes.Length / 1KB, 1)
        Write-Host "  OK    $storagePath ($kb KB)" -ForegroundColor Green
        $ok++
    } catch {
        Write-Host "  FAIL  $storagePath - $($_.Exception.Message)" -ForegroundColor Red
        $fail++
    }
}

Write-Host ""
Write-Host "Done: $ok uploaded, $missing missing local file, $fail failed." -ForegroundColor $(if ($fail -eq 0 -and $missing -eq 0) { "Green" } else { "Yellow" })
if ($missing -gt 0) {
    Write-Host ""
    Write-Host "For MISSING files: find the PDF on your PC or temporarily unblock OLD Supabase billing." -ForegroundColor Yellow
    Write-Host "Common names still needed: AMLCFT LAW.pdf, amlcft cb uae decision.pdf" -ForegroundColor Yellow
}
