# Comply Solutions - first-time installation into your own Azure subscription.
#
# Run once per environment (for example production, then test). Safe to run again: it only creates what is missing
# and re-applies your settings.
#
# Usage (from the unzipped package folder, in PowerShell):
#   .\install.ps1
#   .\install.ps1 -SettingsFile .\settings-test.json

param(
    [string]$SettingsFile = (Join-Path $PSScriptRoot "settings.json")
)

. (Join-Path $PSScriptRoot "lib\common.ps1")

function Get-Required($value, [string]$label) {
    $text = [string]$value
    if (-not $text.Trim() -or $text -match '^REQUIRED') {
        throw "Please fill in '$label' in $SettingsFile."
    }
    return $text.Trim()
}

# Splits a postgresql://user:password@host:port/db connection string into the parts the API needs.
function Split-PostgresUri([string]$uriText) {
    $uri = [System.Uri]$uriText
    $userInfo = $uri.UserInfo
    $sep = $userInfo.IndexOf(':')
    if ($sep -lt 0) { throw "The database connection string has no password. Replace [YOUR-PASSWORD] with your database password." }
    $password = [System.Uri]::UnescapeDataString($userInfo.Substring($sep + 1))
    if ($password -match 'YOUR-PASSWORD') { throw "Replace [YOUR-PASSWORD] in the database connection string with your database password." }
    $port = $uri.Port
    if ($port -le 0) { $port = 5432 }
    $db = $uri.AbsolutePath.Trim('/')
    if (-not $db) { $db = "postgres" }
    return @{
        Host     = $uri.Host
        Port     = "$port"
        User     = [System.Uri]::UnescapeDataString($userInfo.Substring(0, $sep))
        Password = $password
        Database = $db
    }
}

$packageDir = $PSScriptRoot
$package = Get-PackageInfo $packageDir

Write-Host ""
Write-Host "Comply Solutions installer - version $($package.version)" -ForegroundColor White

Write-Step "Checking this computer"
Assert-AzCli
Write-Ok "Azure CLI found"
if (-not (Test-Path $SettingsFile)) {
    throw "Settings file not found: $SettingsFile`nCopy settings-template.json to settings.json and fill it in (README.pdf section 4)."
}
$s = Get-Content $SettingsFile -Raw | ConvertFrom-Json

$subscriptionId = Get-Required $s.azure.subscriptionId "azure.subscriptionId"
$rg = Get-Required $s.azure.resourceGroup "azure.resourceGroup"
$location = Get-Required $s.azure.location "azure.location"
$prefix = (Get-Required $s.azure.namePrefix "azure.namePrefix").ToLowerInvariant()
if ($prefix -notmatch '^[a-z0-9]{3,20}$') { throw "azure.namePrefix must be 3 to 20 lowercase letters or numbers." }
$sku = [string]$s.azure.planSku
if (-not $sku.Trim()) { $sku = "P1v3" }

$sbUrl = (Get-Required $s.supabase.projectUrl "supabase.projectUrl").TrimEnd('/')
$sbAnon = Get-Required $s.supabase.anonKey "supabase.anonKey"
$sbService = Get-Required $s.supabase.serviceRoleKey "supabase.serviceRoleKey"
$sbJwt = Get-Required $s.supabase.jwtSecret "supabase.jwtSecret"
$dbUri = Get-Required $s.supabase.databaseConnectionString "supabase.databaseConnectionString"
$bucket = [string]$s.supabase.storageBucket
if (-not $bucket.Trim()) { $bucket = "doc" }
$aiKey = Get-Required $s.ai.openRouterApiKey "ai.openRouterApiKey"
$diEndpoint = Get-Required $s.ai.documentIntelligenceEndpoint "ai.documentIntelligenceEndpoint"
$diKey = Get-Required $s.ai.documentIntelligenceKey "ai.documentIntelligenceKey"
$aoaiEndpoint = Get-Required $s.ai.azureOpenAIEndpoint "ai.azureOpenAIEndpoint"
$aoaiKey = Get-Required $s.ai.azureOpenAIKey "ai.azureOpenAIKey"
$embeddingDeployment = [string]$s.ai.embeddingDeployment
if (-not $embeddingDeployment.Trim()) { $embeddingDeployment = "text-embedding-3-small" }
$db = Split-PostgresUri $dbUri
Write-Ok "Settings file is complete"

Write-Step "Verifying package files"
Test-PackageChecksums $packageDir

Write-Step "Signing in to Azure"
Connect-Azure $subscriptionId

$planName = "$prefix-cs-plan"
$apiName = "$prefix-cs-api"
$webName = "$prefix-cs-web"

Write-Step "Creating Azure resources (existing ones are kept)"
$rgExists = (Invoke-Az group exists --name $rg).Trim()
if ($rgExists -ne "true") {
    $null = Invoke-Az group create --name $rg --location $location -o none
    Write-Ok "Created resource group $rg"
} else {
    Write-Ok "Resource group $rg already exists"
}

$planFound = Invoke-AzTry appservice plan list --resource-group $rg --query "[?name=='$planName'].name | [0]" -o tsv
if (-not $planFound) {
    $null = Invoke-Az appservice plan create --resource-group $rg --name $planName --location $location --sku $sku -o none
    Write-Ok "Created App Service plan $planName ($sku)"
} else {
    Write-Ok "App Service plan $planName already exists"
}

foreach ($appName in @($apiName, $webName)) {
    if (Test-WebAppExists $rg $appName) {
        Write-Ok "App Service $appName already exists"
        continue
    }
    $null = Invoke-AzTry webapp create --resource-group $rg --plan $planName --name $appName --runtime "dotnet:8" -o none
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create $appName. The name may already be used by another Azure customer; choose a different azure.namePrefix."
    }
    Write-Ok "Created App Service $appName"
}

foreach ($appName in @($apiName, $webName)) {
    $null = Invoke-Az webapp update --resource-group $rg --name $appName --https-only true -o none
    $null = Invoke-Az webapp config set --resource-group $rg --name $appName --always-on true --use-32bit-worker-process false --min-tls-version 1.2 --ftps-state Disabled -o none
}
Write-Ok "HTTPS only, always on, 64-bit"

$apiUrl = "https://" + (Get-AppHost $rg $apiName)
$webUrl = "https://" + (Get-AppHost $rg $webName)

Write-Step "Saving settings into the App Services"
$apiSettings = [ordered]@{
    ASPNETCORE_ENVIRONMENT          = "Production"
    Bcp__UsePostgres                = "true"
    Bcp__MigrateLocalDataToSupabase = "false"
    Bcp__PostgresMaxPoolSize        = "5"
    Bcp__CorsOrigins                = $webUrl
    ConnectionStrings__PostgreSQL   = $dbUri
    Supabase__Url                   = $sbUrl
    Supabase__JwtSecret             = $sbJwt
    Supabase__ServiceRoleKey        = $sbService
    Supabase__DbHost                = $db.Host
    Supabase__DbPort                = $db.Port
    Supabase__DbUser                = $db.User
    Supabase__DbPassword            = $db.Password
    Supabase__DbName                = $db.Database
    Supabase__StorageBucket         = $bucket
    # Analysis (New analysis page) and document reading (Azure Document Intelligence) - the only AI services used.
    OpenRouter__ApiKey                    = $aiKey
    AzureDocumentIntelligence__Endpoint   = $diEndpoint
    AzureDocumentIntelligence__ApiKey     = $diKey
    AzureOpenAI__Endpoint                 = $aoaiEndpoint
    AzureOpenAI__ApiKey                   = $aoaiKey
    AzureOpenAI__EmbeddingDeployment      = $embeddingDeployment.Trim()
    RegulRetrieval__EmbeddingProvider     = "azure-openai"
    # Applies the package's analysis prompts and settings at startup (client-baseline.json).
    ClientBaseline__Enabled               = "true"
    KAFKA_ENABLED                         = "false"
    Integrations__PublicApiUrl            = $apiUrl
}
if ($s.extraApiSettings) {
    foreach ($p in $s.extraApiSettings.PSObject.Properties) { $apiSettings[$p.Name] = [string]$p.Value }
}
Set-AppSettingsFromMap $rg $apiName $apiSettings
Write-Ok "API settings saved ($($apiSettings.Count) values)"

$webSettings = [ordered]@{
    supabaseUrl     = $sbUrl
    supabaseAnonKey = $sbAnon
    ndApiUrl        = $apiUrl
    appUrl          = $webUrl
}
Set-AppSettingsFromMap $rg $webName $webSettings
Write-Ok "Web settings saved"

Publish-Api $packageDir $rg $apiName
Publish-Web $packageDir $rg $webName

Write-Host ""
Write-Host "Installation complete - version $($package.version)" -ForegroundColor Green
Write-Host ""
Write-Host "  Your app:  $webUrl" -ForegroundColor White
Write-Host "  API check: $apiUrl/health/version"
Write-Host ""
Write-Host "Next steps (README.pdf section 6):"
Write-Host "  1. Create your first user in Supabase (Authentication > Users > Add user)."
Write-Host "  2. Sign in once at $webUrl with that user."
Write-Host "  3. Make that user the administrator (README.pdf section 6, one SQL statement)."
Write-Host ""
Write-Host "Keep these values for future updates:  -ResourceGroup $rg  -NamePrefix $prefix" -ForegroundColor Yellow
