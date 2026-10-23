# Build the client delivery package (install + patch) for one version.
#
# Output (in deploy\client-package\out):
#   ComplySolutions-v<version>\          the folder the client gets
#   ComplySolutions-v<version>.zip       the same folder zipped - this is what you send
#   ComplySolutions-v<version>.zip.sha256  send this value separately (email/chat) so the client can check the download
#
# Usage:
#   .\deploy\client-package\build-client-package.ps1 -Version 1.0.0 -Notes "First release"
#   .\deploy\client-package\build-client-package.ps1 -Version 1.2.0 -Notes "Faster analysis; fixed report export"
#   .\deploy\client-package\build-client-package.ps1 -Version 1.2.0 -Force     # rebuild over an existing output
#
# The package is the same for every client. Nothing client-specific and none of our keys go inside it.

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$packageRoot = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $packageRoot -Parent) -Parent
$apiRoot = Join-Path $repoRoot "bcp-api"
$webRoot = Join-Path $repoRoot "bcp-web"
$clientFiles = Join-Path $packageRoot "client-files"
$outRoot = Join-Path $packageRoot "out"
$name = "ComplySolutions-v$Version"
$outDir = Join-Path $outRoot $name
$staging = Join-Path $outRoot "_staging"
$writeZip = Join-Path $apiRoot "scripts\write-deploy-zip.ps1"

function Write-Step([string]$message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

# Windows PowerShell 5.1 turns tool warnings on stderr into errors when output is redirected; check exit codes instead.
function Invoke-Native([scriptblock]$Block) {
    $ErrorActionPreference = "Continue"
    & $Block
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.0" }
if (Test-Path $outDir) {
    if (-not $Force) { throw "$outDir already exists. Use a new version number, or -Force to rebuild it." }
    Remove-Item $outDir -Recurse -Force
}
foreach ($p in @("$outDir.zip", "$outDir.zip.sha256")) { if (Test-Path $p) { Remove-Item $p -Force } }
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $outDir, $staging -Force | Out-Null

Write-Step "Source check"
Push-Location $repoRoot
$commit = ([string](Invoke-Native { git rev-parse --short HEAD })).Trim()
$branch = ([string](Invoke-Native { git rev-parse --abbrev-ref HEAD })).Trim()
$dirty = [bool](Invoke-Native { git status --porcelain -- bcp-api bcp-web })
Pop-Location
Write-Host "  branch $branch, commit $commit"
if ($dirty) {
    Write-Host "  WARNING: bcp-api or bcp-web has uncommitted changes. Release packages should be built from committed code." -ForegroundColor Yellow
}

Write-Step "Version stamp $Version"
$versionArgs = @{ Label = $Version }
if ($Notes.Trim()) { $versionArgs.Notes = $Notes.Trim() }
& (Join-Path $apiRoot "scripts\write-deploy-version.ps1") @versionArgs

Write-Step "Building the backend (dotnet publish)"
$apiOut = Join-Path $staging "api"
Invoke-Native { dotnet publish (Join-Path $apiRoot "Bcp.Api.csproj") -c Release -o $apiOut }
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# Our own settings files must never reach a client: their values come from App Service settings.
$removePatterns = @("appsettings.Secrets*.json", "appsettings.Development*.json", "appsettings.*-old*.json", "appsettings.*.example.json", "*.env", "bcp-api.zip")
foreach ($pattern in $removePatterns) {
    Get-ChildItem -Path $apiOut -Filter $pattern -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item $_.FullName -Force
        Write-Host "  removed $($_.FullName.Substring($apiOut.Length + 1))"
    }
}
# Leftover local build folders inside bcp-api (_publishcheck, _testout, ...) get copied by publish; none belong in a release.
Get-ChildItem -Path $apiOut -Directory -Filter "_*" | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force
    Write-Host "  removed folder $($_.Name)"
}

Write-Step "Exporting our live analysis setup (prompts + analysis settings)"
# Read-only export from the database in appsettings.Development.json; applied on client installs at startup.
Invoke-Native { python -I (Join-Path $packageRoot "export-baseline.py") (Join-Path $apiOut "client-baseline.json") }
if ($LASTEXITCODE -ne 0) { throw "Baseline export failed. Needs Python with: pip install `"psycopg[binary]`"" }

Write-Step "Building the frontend (npm run build:prod)"
Push-Location $webRoot
Invoke-Native { npm run build:prod }
$npmExit = $LASTEXITCODE
Pop-Location
if ($npmExit -ne 0) { throw "npm run build:prod failed" }
$webOut = Join-Path $staging "web"
Copy-Item (Join-Path $webRoot "dist\reguliq-web\browser") $webOut -Recurse
# Each installation writes its own app-config.json at deploy time.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText((Join-Path $webOut "assets\app-config.json"), "{}", $utf8NoBom)

Write-Step "Checking that none of our keys are inside the build"
$devSettings = Join-Path $apiRoot "appsettings.Development.json"
$secrets = @()
if (Test-Path $devSettings) {
    $dev = Get-Content $devSettings -Raw | ConvertFrom-Json
    $candidates = @($dev.Supabase.JwtSecret, $dev.Supabase.ServiceRoleKey, $dev.Supabase.DbPassword, $dev.ConnectionStrings.PostgreSQL, $dev.OpenRouter.ApiKey, $dev.Gemini.ApiKey, $dev.LandingAi.ApiKey, $dev.AzureDocumentIntelligence.ApiKey, $dev.AzureOpenAI.ApiKey)
    foreach ($c in $candidates) { if ($c -and ([string]$c).Length -ge 12) { $secrets += [string]$c } }
} else {
    Write-Host "  appsettings.Development.json not found - only the file-name check ran." -ForegroundColor Yellow
}
$textFiles = Get-ChildItem -Path $staging -Recurse -File -Include *.json, *.config, *.js, *.txt, *.xml
$leaks = @()
foreach ($f in $textFiles) {
    $content = [System.IO.File]::ReadAllText($f.FullName)
    foreach ($secret in $secrets) {
        if ($content.Contains($secret)) { $leaks += $f.FullName.Substring($staging.Length + 1) }
    }
}
if ($leaks.Count -gt 0) {
    throw "Our keys were found in: $(($leaks | Select-Object -Unique) -join ', '). Package NOT created."
}
Write-Host "  No keys found ($($textFiles.Count) files checked against $($secrets.Count) known secrets)." -ForegroundColor Green

Write-Step "Zipping backend and frontend"
& $writeZip -SourceDir $apiOut -ZipPath (Join-Path $outDir "backend.zip")
& $writeZip -SourceDir $webOut -ZipPath (Join-Path $outDir "frontend.zip")

Write-Step "Adding scripts and settings template"
Copy-Item (Join-Path $clientFiles "install.ps1") $outDir
Copy-Item (Join-Path $clientFiles "deploy-patch.ps1") $outDir
Copy-Item (Join-Path $clientFiles "settings-template.json") $outDir
Copy-Item (Join-Path $clientFiles "lib") (Join-Path $outDir "lib") -Recurse

$builtAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$info = [ordered]@{ product = "Comply Solutions"; version = $Version; builtAt = $builtAt; commit = $commit }
[System.IO.File]::WriteAllText((Join-Path $outDir "version.json"), ($info | ConvertTo-Json), $utf8NoBom)

$releaseDate = (Get-Date).ToString("d MMMM yyyy")
$changelog = "Comply Solutions $Version - $releaseDate`r`n`r`n"
if ($Notes.Trim()) { $changelog += ($Notes.Trim() -split ';\s*' | ForEach-Object { "- $_" }) -join "`r`n" } else { $changelog += "- Maintenance update" }
$changelog += "`r`n`r`nTo install this update: README.pdf section 7.`r`n"
[System.IO.File]::WriteAllText((Join-Path $outDir "CHANGELOG.txt"), $changelog, $utf8NoBom)

Write-Step "Creating README.pdf"
$html = Get-Content (Join-Path $clientFiles "README.html") -Raw -Encoding UTF8
$html = $html.Replace("{{VERSION}}", $Version).Replace("{{DATE}}", $releaseDate)
$htmlTemp = Join-Path $staging "README.html"
[System.IO.File]::WriteAllText($htmlTemp, $html, $utf8NoBom)
$pdfPath = Join-Path $outDir "README.pdf"
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge not found; it is used to print README.pdf." }
$edgeProfile = Join-Path $staging "edge-profile"
$edgeArgs = @("--headless=new", "--disable-gpu", "--no-pdf-header-footer", "--user-data-dir=`"$edgeProfile`"", "--print-to-pdf=`"$pdfPath`"", ([System.Uri]$htmlTemp).AbsoluteUri)
Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
for ($i = 0; $i -lt 30 -and -not (Test-Path $pdfPath); $i++) { Start-Sleep -Seconds 1 }
if (-not (Test-Path $pdfPath)) { throw "README.pdf was not created." }
Write-Host "  README.pdf created" -ForegroundColor Green

Write-Step "Writing checksums"
$checked = @("backend.zip", "frontend.zip", "install.ps1", "deploy-patch.ps1", "lib\common.ps1")
$lines = foreach ($file in $checked) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path (Join-Path $outDir $file)).Hash.ToLowerInvariant()
    "$hash  $file"
}
[System.IO.File]::WriteAllText((Join-Path $outDir "checksums.txt"), (($lines -join "`r`n") + "`r`n"), $utf8NoBom)

Write-Step "Zipping the package"
& $writeZip -SourceDir $outDir -ZipPath "$outDir.zip"
$zipHash = (Get-FileHash -Algorithm SHA256 -Path "$outDir.zip").Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$outDir.zip.sha256", "$zipHash  $name.zip`r`n", $utf8NoBom)
Remove-Item $staging -Recurse -Force

Write-Host ""
Write-Host "Package ready: $outDir.zip" -ForegroundColor Green
Write-Host "  SHA-256 (send separately to the client): $zipHash"
Write-Host "  Contents:"
Get-ChildItem $outDir -Recurse -File | ForEach-Object { Write-Host ("    {0,-28} {1,10:N0} bytes" -f $_.FullName.Substring($outDir.Length + 1), $_.Length) }
