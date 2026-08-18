# Smoke-test local API after restart. Fails fast if bootstrap, auth, or dashboard endpoints hang.
param(
    [string]$Api = "http://localhost:5100",
    [string]$Email = "superadmin@reguliq.com",
    [string]$Password = "123456",
    [int]$BootstrapWaitSec = 120
)

$ErrorActionPreference = "Stop"
$script:failed = $false

function Fail([string]$msg) {
    Write-Host "FAIL: $msg" -ForegroundColor Red
    $script:failed = $true
}

Write-Host "=== BCP API check ($Api) ==="

$ready = $false
$deadline = (Get-Date).AddSeconds($BootstrapWaitSec)
while ((Get-Date) -lt $deadline) {
    try {
        $h = Invoke-RestMethod -Uri "$Api/health/startup" -TimeoutSec 10
        if ($h.status -eq "ready") { $ready = $true; Write-Host "bootstrap: ready"; break }
        if ($h.status -eq "failed") { Fail "bootstrap failed: $($h.error)"; break }
        Write-Host "bootstrap: $($h.status) ..."
    } catch {
        Write-Host "waiting for API ..."
    }
    Start-Sleep -Seconds 2
}
if (-not $ready -and -not $script:failed) { Fail "bootstrap not ready within ${BootstrapWaitSec}s" }

$listeners = @(Get-NetTCPConnection -LocalPort 5100 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -eq 0) { Fail "nothing listening on port 5100" }
elseif ($listeners.Count -gt 1) { Fail "multiple listeners on port 5100 ($($listeners.Count))" }
else { Write-Host "port 5100: single instance (PID $($listeners[0].OwningProcess))" }

try {
    $root = Invoke-RestMethod -Uri $Api -TimeoutSec 15
    Write-Host "GET / OK persistence=$($root.persistence)"
} catch { Fail "GET / - $($_.Exception.Message)" }

$sb = "https://prxmkrmwqxlltwjnazay.supabase.co"
$anon = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InByeG1rcm13cXhsbHR3am5hemF5Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODY5NzAwMTIsImV4cCI6MjEwMjU0NjAxMn0.nHcayH4ul9rcluW8yqzvWTEXgh-jHC6hU4WL2YauVAw"
$token = $null
try {
    $authBody = @{ email = $Email; password = $Password } | ConvertTo-Json
    $auth = Invoke-RestMethod -Uri "$sb/auth/v1/token?grant_type=password" -Method POST `
        -Headers @{ apikey = $anon; Authorization = "Bearer $anon"; "Content-Type" = "application/json" } `
        -Body $authBody -TimeoutSec 30
    $token = $auth.access_token
    Write-Host "Supabase auth OK"
} catch { Fail "Supabase auth - $($_.Exception.Message)" }

try {
    $dbStatus = Invoke-RestMethod -Uri "$Api/dev/db-status" -TimeoutSec 30
    Write-Host "db-status: $($dbStatus.pgSessionsForDatabase) pg sessions, idle-in-tx=$($dbStatus.idleInTransaction), workers=$($dbStatus.inProcessAnalysisWorkers)"
    if ($dbStatus.diagnosis) { $dbStatus.diagnosis | ForEach-Object { Write-Host "  -> $_" } }
} catch { Write-Host "db-status skipped: $($_.Exception.Message)" }

if ($token) {
    $checks = @(
        @{ Name = "/nd/auth/me"; Url = "$Api/nd/auth/me"; TimeoutSec = 45; MaxMs = 40000 },
        @{ Name = "analysis-runs summary"; Url = "${Api}/nd/analysis-runs?ndOnly=true&summaryOnly=true"; TimeoutSec = 60; MaxMs = 55000 },
        @{ Name = "nav-counts"; Url = "$Api/nd/workspace/nav-counts"; TimeoutSec = 45; MaxMs = 40000 }
    )
    foreach ($check in $checks) {
        try {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $null = Invoke-RestMethod -Uri $check.Url -Headers @{ Authorization = "Bearer $token" } -TimeoutSec $check.TimeoutSec
            $sw.Stop()
            $elapsed = $sw.ElapsedMilliseconds
            if ($elapsed -gt $check.MaxMs) {
                Fail "$($check.Name) slow (${elapsed}ms, limit $($check.MaxMs)ms)"
            } else {
                Write-Host "$($check.Name) OK (${elapsed}ms)" -ForegroundColor Green
            }
        } catch { Fail "$($check.Name) - $($_.Exception.Message)" }
    }
}

if ($script:failed) { exit 1 }
Write-Host ""
Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
