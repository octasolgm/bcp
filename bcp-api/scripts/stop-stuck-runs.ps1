# Cancel ND analysis runs stuck in running/processing (no login required).
# Run: .\scripts\stop-stuck-runs.ps1

$ErrorActionPreference = "Stop"
$api = "http://localhost:5100"

Write-Host "Stopping stuck analysis runs via $api/dev/stop-stuck-runs ..." -ForegroundColor Cyan
try {
    $res = Invoke-RestMethod -Uri "$api/dev/stop-stuck-runs" -Method Post -TimeoutSec 120
    Write-Host $res.message -ForegroundColor Green
}
catch {
    Write-Error "API not reachable on $api. Run .\scripts\restart-api.ps1 -Detached first."
}
