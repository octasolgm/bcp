# Create an Azure Kudu-compatible zip (index.html at zip root, forward-slash paths).
# Avoids PowerShell Compress-Archive + VS Code zip issues (e.g. repo paths with spaces).
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDir,
    [Parameter(Mandatory = $true)]
    [string]$ZipPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceFull = (Resolve-Path $SourceDir).Path.TrimEnd('\')
$zipFull = [System.IO.Path]::GetFullPath($ZipPath)
$zipDir = Split-Path $zipFull -Parent
if (-not (Test-Path $zipDir)) {
    New-Item -ItemType Directory -Path $zipDir -Force | Out-Null
}
if (Test-Path $zipFull) {
    Remove-Item $zipFull -Force
}

$zipStream = [System.IO.File]::Open($zipFull, [System.IO.FileMode]::CreateNew)
$zip = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)

try {
    foreach ($file in (Get-ChildItem -Path $sourceFull -Recurse -File)) {
        $relative = $file.FullName.Substring($sourceFull.Length + 1).Replace('\', '/')
        $entry = $zip.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $entryStream = $entry.Open()
        try {
            $fileStream = [System.IO.File]::OpenRead($file.FullName)
            try {
                $fileStream.CopyTo($entryStream)
            } finally {
                $fileStream.Close()
            }
        } finally {
            $entryStream.Close()
        }
    }
} finally {
    $zip.Dispose()
    $zipStream.Close()
}

# Validate — same check Kudu uses before extracting.
$testZip = [System.IO.Compression.ZipFile]::OpenRead($zipFull)
$count = $testZip.Entries.Count
$testZip.Dispose()
if ($count -eq 0) {
    throw "Zip has no entries: $zipFull"
}

$bytes = (Get-Item $zipFull).Length
Write-Host "Created $zipFull ($count files, $bytes bytes)"
