# Shared helpers for install.ps1 and deploy-patch.ps1 (dot-sourced).
# Runs on Windows PowerShell 5.1 and PowerShell 7. Needs Azure CLI (az).

$ErrorActionPreference = "Stop"

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Write-Ok([string]$message) {
    Write-Host "    $message" -ForegroundColor Green
}

function Write-Note([string]$message) {
    Write-Host "    $message" -ForegroundColor Yellow
}

function Assert-AzCli {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        Write-Host ""
        Write-Host "Azure CLI is not installed on this computer." -ForegroundColor Red
        Write-Host "Install it, close this window, open a new PowerShell window and run the script again:"
        Write-Host "  winget install -e --id Microsoft.AzureCLI"
        Write-Host "  or download: https://aka.ms/installazurecliwindows"
        exit 1
    }
}

# Runs az and throws if it fails. Returns stdout text.
# az prints warnings on stderr; Windows PowerShell 5.1 can treat those as errors, so the exit code decides instead.
function Invoke-Az {
    $ErrorActionPreference = "Continue"
    $out = & az @args
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI command failed: az $($args -join ' ')"
    }
    return $out
}

function Connect-Azure([string]$subscriptionId) {
    $ErrorActionPreference = "Continue"
    $null = & az account show -o none 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Note "A browser window will open. Sign in with your Azure account."
        $null = Invoke-Az login -o none
    }
    if ($subscriptionId) {
        $null = Invoke-Az account set --subscription $subscriptionId
    }
    $account = Invoke-Az account show -o json | Out-String | ConvertFrom-Json
    Write-Ok "Signed in as $($account.user.name)"
    Write-Ok "Subscription: $($account.name) ($($account.id))"
}

# Runs az without throwing; the caller checks $LASTEXITCODE.
function Invoke-AzTry {
    $ErrorActionPreference = "Continue"
    return (& az @args 2>$null)
}

function Get-PackageInfo([string]$packageDir) {
    $path = Join-Path $packageDir "version.json"
    if (-not (Test-Path $path)) { throw "version.json not found in $packageDir. Is the package complete?" }
    return Get-Content $path -Raw | ConvertFrom-Json
}

# checksums.txt lines: "<sha256>  <file name>"
function Test-PackageChecksums([string]$packageDir) {
    $list = Join-Path $packageDir "checksums.txt"
    if (-not (Test-Path $list)) { throw "checksums.txt not found. The package is incomplete." }
    foreach ($line in Get-Content $list) {
        if (-not $line.Trim()) { continue }
        $parts = $line -split '\s+', 2
        $expected = $parts[0].Trim().ToLowerInvariant()
        $file = Join-Path $packageDir $parts[1].Trim()
        if (-not (Test-Path $file)) { throw "Missing package file: $($parts[1])" }
        $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "File $($parts[1]) does not match the checksum. Do not continue; ask your vendor for a fresh copy of the package."
        }
        Write-Ok "Verified $($parts[1])"
    }
}

function Test-WebAppExists([string]$rg, [string]$name) {
    $ErrorActionPreference = "Continue"
    $found = & az webapp list --resource-group $rg --query "[?name=='$name'].name | [0]" -o tsv 2>$null
    return ($LASTEXITCODE -eq 0 -and $found -and $found.Trim() -eq $name)
}

function Get-AppHost([string]$rg, [string]$name) {
    return (Invoke-Az webapp show --resource-group $rg --name $name --query defaultHostName -o tsv).Trim()
}

function Get-AppSettingsMap([string]$rg, [string]$name) {
    $list = Invoke-Az webapp config appsettings list --resource-group $rg --name $name -o json | Out-String | ConvertFrom-Json
    $map = @{}
    foreach ($item in $list) { $map[$item.name] = [string]$item.value }
    return $map
}

# Writes settings through a temp file so values with special characters survive, then deletes the file.
function Set-AppSettingsFromMap([string]$rg, [string]$name, [System.Collections.IDictionary]$map) {
    $items = @()
    foreach ($key in $map.Keys) {
        $items += [ordered]@{ name = $key; value = [string]$map[$key]; slotSetting = $false }
    }
    $tmp = Join-Path $env:TEMP ("bcp-settings-" + [guid]::NewGuid().ToString("N") + ".json")
    try {
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($tmp, (ConvertTo-Json -InputObject $items -Depth 3), $utf8NoBom)
        $null = Invoke-Az webapp config appsettings set --resource-group $rg --name $name --settings "@$tmp" -o none
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

function Publish-Zip([string]$rg, [string]$name, [string]$zipPath) {
    $ErrorActionPreference = "Continue"
    $maxAttempts = 4
    for ($i = 1; $i -le $maxAttempts; $i++) {
        & az webapp deploy --resource-group $rg --name $name --src-path $zipPath --type zip --async false -o none
        if ($LASTEXITCODE -eq 0) {
            Write-Ok "Uploaded to $name"
            return
        }
        if ($i -lt $maxAttempts) {
            $wait = 20 * $i
            Write-Note "Upload failed (attempt $i of $maxAttempts). Retrying in $wait seconds..."
            Start-Sleep -Seconds $wait
        }
    }
    throw "Could not upload to $name after $maxAttempts attempts."
}

# The API reports "ready" once its database tables are created or upgraded.
function Wait-ApiReady([string]$apiHost, [int]$maxSeconds = 600) {
    $url = "https://$apiHost/health/startup"
    $deadline = (Get-Date).AddSeconds($maxSeconds)
    Write-Note "Waiting for the API to start and prepare the database (can take a few minutes on first install)..."
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            if ($r.StatusCode -eq 200 -and $r.Content -match '"ready"') {
                Write-Ok "API is ready."
                return
            }
        } catch {
            $body = $null
            if ($_.Exception.Response) {
                try {
                    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                    $body = $reader.ReadToEnd()
                } catch { }
            }
            if ($body -and $body -match '"failed"') {
                throw "The API could not prepare the database: $body`nCheck the database values in your settings file."
            }
        }
        Start-Sleep -Seconds 10
    }
    throw "The API did not report ready within $maxSeconds seconds. Open $url in a browser to see its status."
}

function Get-ApiVersion([string]$apiHost) {
    try {
        $r = Invoke-RestMethod -Uri "https://$apiHost/health/version" -TimeoutSec 20
        if ($r.label) { return [string]$r.label }
    } catch { }
    return "unknown"
}

# Copies the frontend zip and writes this installation's assets/app-config.json into the copy.
function New-ConfiguredWebZip([string]$sourceZip, [System.Collections.IDictionary]$config) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $outZip = Join-Path $env:TEMP ("bcp-web-" + [guid]::NewGuid().ToString("N") + ".zip")
    Copy-Item $sourceZip $outZip
    $zip = [System.IO.Compression.ZipFile]::Open($outZip, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $existing = $zip.GetEntry("assets/app-config.json")
        if ($existing) { $existing.Delete() }
        $entry = $zip.CreateEntry("assets/app-config.json")
        $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding $false))
        try { $writer.Write((ConvertTo-Json -InputObject $config -Compress)) } finally { $writer.Dispose() }
    } finally {
        $zip.Dispose()
    }
    return $outZip
}

# Values the web app needs at runtime. Stored as web app settings at install so patches can reuse them.
function Get-WebConfigFromSettings([System.Collections.IDictionary]$settings) {
    foreach ($key in @("supabaseUrl", "supabaseAnonKey", "ndApiUrl", "appUrl")) {
        if (-not $settings[$key]) { throw "Web app setting '$key' is missing. Run install.ps1 first." }
    }
    return [ordered]@{
        supabaseUrl     = $settings["supabaseUrl"]
        supabaseAnonKey = $settings["supabaseAnonKey"]
        ndApiUrl        = $settings["ndApiUrl"]
        appUrl          = $settings["appUrl"]
        edition         = "client"
    }
}

function Publish-Api([string]$packageDir, [string]$rg, [string]$apiName) {
    Write-Step "Deploying the backend (API) to $apiName"
    Publish-Zip $rg $apiName (Join-Path $packageDir "backend.zip")
    $null = Invoke-Az webapp restart --resource-group $rg --name $apiName -o none
    Wait-ApiReady (Get-AppHost $rg $apiName)
}

function Publish-Web([string]$packageDir, [string]$rg, [string]$webName) {
    Write-Step "Deploying the frontend (web) to $webName"
    $config = Get-WebConfigFromSettings (Get-AppSettingsMap $rg $webName)
    $zip = New-ConfiguredWebZip (Join-Path $packageDir "frontend.zip") $config
    try {
        Publish-Zip $rg $webName $zip
    } finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
}
