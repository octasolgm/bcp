# Stop whatever holds port 5100, rebuild Bcp.Api, and start it (Development).
# Usage: .\scripts\restart-api.ps1           # rebuild + run in this window
#        .\scripts\restart-api.ps1 -Detached # rebuild + start hidden background process

param(
    [switch]$Detached,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root "Bcp.Api.csproj"
$port = 5100

function Stop-ApiOnPort {
    param([int]$ListenPort)
    $conns = Get-NetTCPConnection -LocalPort $ListenPort -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        $procId = $c.OwningProcess
        if (-not $procId) { continue }
        Write-Host "Stopping process $procId on port $ListenPort..."
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    }
    Get-CimInstance Win32_Process -Filter "Name='Bcp.Api.exe'" -ErrorAction SilentlyContinue |
        ForEach-Object {
            Write-Host "Stopping Bcp.Api.exe ($($_.ProcessId))..."
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Seconds 2
}

Stop-ApiOnPort -ListenPort $port

if (-not $NoBuild) {
    Write-Host "Building $project ..."
    dotnet build $project -v q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if ($Detached) {
    Write-Host "Starting API in a new window..."
    $runCmd = "Set-Location '$root'; dotnet run --project '$project' --no-build"
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit", "-Command", $runCmd
    Write-Host "API starting on http://localhost:$port (check the new PowerShell window)."
    exit 0
}

Write-Host "Starting API (foreground). Ctrl+C to stop."
Set-Location $root
dotnet run --project $project --no-build
