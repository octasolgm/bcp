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
$dllDir = Join-Path $root "bin\Debug\net8.0"
$dll = Join-Path $dllDir "Bcp.Api.dll"
$port = 5100

function Get-DotnetExe {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = "C:\Program Files\dotnet\dotnet.exe"
    if (Test-Path $fallback) { return $fallback }
    throw "dotnet not found. Install .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0"
}

function Test-DotnetSdk {
    $sdks = & (Get-DotnetExe) --list-sdks 2>&1
    return ($LASTEXITCODE -eq 0 -and @($sdks).Count -gt 0)
}

function Set-RollForwardEnv {
    $env:DOTNET_ROLL_FORWARD = "Major"
    $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = "1"
}

function Unblock-ApiBinaries {
    $bin = Join-Path $root "bin\Debug\net8.0"
    if (-not (Test-Path $bin)) { return }
    Write-Host "Unblocking build output in $bin ..."
    Get-ChildItem $bin -File -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
    }
}

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

$dotnet = Get-DotnetExe
$hasSdk = Test-DotnetSdk

if (-not $NoBuild) {
    if ($hasSdk) {
        Write-Host "Building $project ..."
        & $dotnet build $project -v q
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    elseif (Test-Path $dll) {
        Write-Host "No .NET SDK found; using existing build at $dllDir (roll-forward to installed runtime)."
        Set-RollForwardEnv
    }
    else {
        Write-Error "No .NET SDK and no pre-built API at $dll. Install .NET 8 SDK or build once on a machine with the SDK."
    }
}

Unblock-ApiBinaries

if ($Detached) {
    Write-Host "Starting API in a new window..."
    if ($hasSdk) {
        $runCmd = "Set-Location '$root'; Get-ChildItem .\bin\Debug\net8.0 -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; & '$dotnet' run --project '$project' --no-build"
    }
    else {
        $runCmd = "Set-Location '$dllDir'; `$env:DOTNET_ROLL_FORWARD='Major'; `$env:DOTNET_ROLL_FORWARD_TO_PRERELEASE='1'; Get-ChildItem . -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; & '$dotnet' Bcp.Api.dll"
    }
    Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit", "-Command", $runCmd
    Write-Host "API starting on http://localhost:$port (check the new PowerShell window)."
    Write-Host "If it exits immediately with 'Application Control policy has blocked', disable Smart App Control or allow dotnet/Bcp.Api in Windows Security."
    exit 0
}

Write-Host "Starting API (foreground). Ctrl+C to stop."
if ($hasSdk) {
    Set-Location $root
    & $dotnet run --project $project --no-build
}
else {
    Set-RollForwardEnv
    Set-Location $dllDir
    & $dotnet Bcp.Api.dll
}
