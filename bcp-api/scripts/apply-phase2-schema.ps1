# Build one SQL file for Phase 2 (fresh Comply-Solution Supabase schema).
# Run the output in Supabase Dashboard -> SQL -> New query -> Run.
# SAFE: creates empty schema on NEW project only — does NOT touch old Supabase data.
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "supabase\phase2-full-schema.sql")
)

$ErrorActionPreference = "Stop"
$supabaseDir = Join-Path $PSScriptRoot "supabase"

$ordered = @(
    "000_phase2_cleanup_partial_bootstrap.sql",
    "002_compliance_sessions.sql",
    "003_dual_verify_kafka.sql",
    "004_bcp_api_extra_columns.sql",
    "001_stored_documents_base.sql",
    "004b_bcp_api_support_tables.sql",
    "005_enterprise_platform.sql",
    "017_nd_supplemental_tables.sql",
    "006_point_classification.sql",
    "007_analysis_run_soft_delete.sql",
    "008_regulation_point_status.sql",
    "009_regul_workflow.sql",
    "010_internal_document_sections.sql",
    "011_landing_ai_extract_cache_schema_key.sql",
    "012_analysis_runs_list_perf.sql",
    "014_temp_point_review_comments.sql",
    "015_analysis_runs_cancelled_status.sql",
    "016_analysis_action_plans.sql",
    "018_demo_analysis_templates.sql"
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("-- BCP Phase 2 FULL schema (comply-solution-project / prxmkrmwqxlltwjnazay)")
[void]$sb.AppendLine("-- SAFE: empty schema on NEW project. Old users/data stay on old project until Phase B import.")
[void]$sb.AppendLine("-- Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$sb.AppendLine()

foreach ($name in $ordered) {
    $path = Join-Path $supabaseDir $name
    if (-not (Test-Path $path)) {
        Write-Warning "Missing $name - skipped"
        continue
    }
    [void]$sb.AppendLine("-- ===============================================================")
    [void]$sb.AppendLine("-- $name")
    [void]$sb.AppendLine("-- ===============================================================")
    [void]$sb.AppendLine((Get-Content $path -Raw))
    [void]$sb.AppendLine()
}

$text = $sb.ToString()
Set-Content -Path $OutFile -Value $text -Encoding UTF8
$sizeKb = [math]::Round((Get-Item $OutFile).Length / 1024, 1)
Write-Host "Wrote $OutFile (${sizeKb} KB)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Open https://supabase.com/dashboard/project/prxmkrmwqxlltwjnazay/sql/new"
Write-Host "  2. Paste phase2-full-schema.sql and click Run"
Write-Host "  3. Storage: create private bucket named doc"
Write-Host "  4. Auth: create user superadmin@reguliq.com"
Write-Host "  5. Restart API: .\scripts\restart-api.ps1 -Detached"
Write-Host ""
Write-Host "Data migration (users, records, demo runs) happens LATER from old project when accessible."
