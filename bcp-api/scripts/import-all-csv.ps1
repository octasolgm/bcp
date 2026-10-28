# Import all exported CSVs from OLD Supabase into NEW (dependency order).
# Put CSV files in one folder (default: Downloads). Names like: analysis_runs_rows.csv
#
# Usage:
#   .\scripts\import-all-csv.ps1
#   .\scripts\import-all-csv.ps1 -CsvFolder "C:\Users\Pc\Downloads" -SkipExisting
#   .\scripts\import-all-csv.ps1 -Only analysis_points,action_plan_history

param(
    [string]$CsvFolder = "$env:USERPROFILE\Downloads",
    [switch]$SkipExisting,
    [string[]]$Only = @()
)

$ErrorActionPreference = "Stop"
$scriptDir = $PSScriptRoot
$importScript = Join-Path $scriptDir "import-csv-table.ps1"

# Table name -> preferred CSV filename patterns (first match wins)
$order = @(
    @{ Table = "departments";           Files = @("departments_rows.csv") },
    @{ Table = "stored_documents";      Files = @("stored_documents_rows.csv", "stored_documents_rows (1).csv") },
    @{ Table = "nd_system_settings";    Files = @("nd_system_settings_rows.csv") },
    @{ Table = "regulation_documents";  Files = @("regulation_documents_rows.csv") },
    @{ Table = "regulation_points";     Files = @("regulation_points_rows.csv") },
    @{ Table = "libraries";             Files = @("libraries_rows.csv") },
    @{ Table = "library_points";        Files = @("library_points_rows.csv") },
    @{ Table = "dual_verify_sessions";   Files = @("dual_verify_sessions_rows.csv") },
    @{ Table = "dual_verify_point_jobs"; Files = @("dual_verify_point_jobs_rows.csv") },
    @{ Table = "analysis_runs";         Files = @("analysis_runs_rows.csv", "analysis_runs_rows (1).csv") },
    @{ Table = "analysis_points";       Files = @("analysis_points_rows.csv", "analysis_points_rows (1).csv", "analysis_points_rows (2).csv") },
    @{ Table = "action_plan_history";   Files = @("action_plan_history_rows.csv") },
    @{ Table = "analysis_reviews";      Files = @("analysis_reviews_rows.csv") },
    @{ Table = "analysis_point_comments"; Files = @("analysis_point_comments_rows.csv") },
    @{ Table = "analysis_status_history"; Files = @("analysis_status_history_rows.csv") },
    @{ Table = "temp_point_review_comments"; Files = @("temp_point_review_comments_rows.csv") },
    @{ Table = "analysis_action_plans"; Files = @("analysis_action_plans_rows.csv") },
    @{ Table = "analysis_action_plan_assignees"; Files = @("analysis_action_plan_assignees_rows.csv") },
    @{ Table = "demo_analysis_templates"; Files = @("demo_analysis_templates_rows.csv") },
    @{ Table = "demo_analysis_template_points"; Files = @("demo_analysis_template_points_rows.csv") }
)

function Find-Csv([string[]]$names) {
    foreach ($name in $names) {
        $path = Join-Path $CsvFolder $name
        if (Test-Path $path) { return $path }
    }
    return $null
}

Write-Host "=== Import all CSVs ===" -ForegroundColor Cyan
Write-Host "Folder: $CsvFolder"
Write-Host ""

$imported = 0
$skipped = 0
$missing = 0

foreach ($item in $order) {
    $table = $item.Table
    if ($Only.Count -gt 0 -and ($Only -notcontains $table)) { continue }

    $csv = Find-Csv $item.Files
    if (-not $csv) {
        Write-Host "SKIP (no CSV): $table" -ForegroundColor DarkYellow
        $missing++
        continue
    }

    if ($SkipExisting) {
        Write-Host "Check $table ..." -NoNewline
        # import-csv-table always appends; user should truncate manually or use -TruncateDest per table
    }

    Write-Host "IMPORT: $table <- $(Split-Path $csv -Leaf)" -ForegroundColor Green
    & $importScript -Table $table -CsvPath $csv
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $table (exit $LASTEXITCODE)" -ForegroundColor Red
        Write-Host "Fix error, then re-run with: .\scripts\import-all-csv.ps1 -Only $table"
        exit $LASTEXITCODE
    }
    $imported++
}

Write-Host ""
Write-Host "Done. Imported: $imported | Missing CSV: $missing" -ForegroundColor Cyan
Write-Host "Run verify SQL in Supabase (see scripts/supabase/021_post_csv_import_verify.sql)"
