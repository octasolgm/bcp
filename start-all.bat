@echo off
REM start-all.bat — helper to manage local dev lifecycle for this repo
REM Usage: start-all.bat start|restart|stopports|build|deploy|build-deploy

setlocal enabledelayedexpansion
set ROOT=%~dp0
rem Trim trailing backslash if present
if "%ROOT:~-1%"=="\" set ROOT=%ROOT:~0,-1%

if "%~1"=="" goto usage
set CMD=%~1

if /I "%CMD%"=="start" goto start_all
if /I "%CMD%"=="restart" goto restart_all
if /I "%CMD%"=="stopports" goto stop_ports
if /I "%CMD%"=="build" goto build_all
if /I "%CMD%"=="deploy" goto deploy_all
if /I "%CMD%"=="build-deploy" goto build_deploy
goto usage

:start_all
echo Starting API (detached) and Web (dev server)...
echo Starting API via PowerShell restart script (detached)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Start-Process powershell -ArgumentList '-NoExit','-Command','Set-Location -LiteralPath \"%ROOT%\\bcp-api\"; .\\scripts\\restart-api.ps1 -Detached' -WindowStyle Hidden"
timeout /t 1 >nul
echo Starting Angular dev server in a new window...
start "bcp-web" cmd /k "cd /d \"%ROOT%\\bcp-web\" && npm start"
goto end

:restart_all
echo Restarting: will attempt to stop runtime hosts then start again.
call :stop_ports
call :start_all
goto end

:stop_ports
echo Stopping common dev ports (3002, 5100) if processes are owned locally.
powershell -NoProfile -Command ^
  "foreach($p in 3002,5100) { $cons = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue; foreach($c in $cons) { if ($c.OwningProcess -and $c.OwningProcess -ne 0) { Write-Host 'Stopping process' $c.OwningProcess 'listening on port' $p; try { Stop-Process -Id $c.OwningProcess -Force -ErrorAction Stop } catch { Write-Host 'Could not stop process' $c.OwningProcess } } } }"
echo Done. Give the OS a moment to release ports.
goto :eof

:build_all
echo Building API (dotnet)...
pushd "%ROOT%\bcp-api" >nul 2>&1
if exist "Bcp.Api.csproj" (
  dotnet build "Bcp.Api.csproj"
) else (
  echo WARNING: Bcp.Api.csproj not found in %CD%
)
popd >nul 2>&1

echo Building Web (npm)...
pushd "%ROOT%\bcp-web" >nul 2>&1
if exist "package.json" (
  if exist "package-lock.json" ( npm ci ) else ( npm install )
  rem use production build script if available
  npm run build:prod 2>nul || npm run build 2>nul || echo \"No build script found; check package.json\"
) else (
  echo WARNING: package.json not found in %CD%
)
popd >nul 2>&1
goto end

:deploy_all
echo Deploying API and Web (placeholders)...
if exist "%ROOT%\bcp-api\scripts\deploy.ps1" (
  echo Deploying API via bcp-api\\scripts\\deploy.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\bcp-api\scripts\deploy.ps1"
) else (
  echo No API deploy script found at bcp-api\\scripts\\deploy.ps1 — add your deploy steps here.
)

if exist "%ROOT%\bcp-web\scripts\deploy.ps1" (
  echo Deploying Web via bcp-web\\scripts\\deploy.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\bcp-web\scripts\deploy.ps1"
) else (
  echo No Web deploy script found at bcp-web\\scripts\\deploy.ps1 — add your deploy steps here.
)
goto end

:build_deploy
call :build_all
call :deploy_all
goto end

:usage
echo Usage: %~nx0 ^(start ^| restart ^| stopports ^| build ^| deploy ^| build-deploy^)
echo.
echo Commands:
echo   start        Start API (detached) and web dev server in new window.
echo   restart      Stop ports then start both services.
echo   stopports    Attempt to stop processes listening on ports 3002 and 5100.
echo   build        Build API and Web (dotnet build + npm build).
echo   deploy       Run deploy scripts if present (placeholders if not).
echo   build-deploy Build then deploy.
goto end

:end
endlocal
exit /b 0
@echo off
echo ===================================
echo   Restarting BCP API and BCP Web
echo ===================================

:: 1. Kill existing dotnet and node processes to clear ports
echo Stopping existing processes...
taskkill /F /IM dotnet.exe 2>nul
taskkill /F /IM node.exe 2>nul

:: Wait 2 seconds to release ports
timeout /t 2 /nobreak >nul

:: 2. Start BCP API in a new window
echo Starting BCP API...
start "BCP API" cmd /k "cd /d "%~dp0bcp-api" && dotnet run"

:: 3. Start BCP Web in a new window (Angular uses npm start)
echo Starting BCP Web...
start "BCP Web" cmd /k "cd /d "%~dp0bcp-web" && npm start"

echo ===================================
echo   Both services starting up!
echo ===================================