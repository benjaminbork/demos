#requires -Version 7
<#
.SYNOPSIS
  Deploys Audio Blog to Azure: infrastructure (Bicep), container images (built in ACR), container apps, frontend.

.EXAMPLE
  ./scripts/deploy.ps1 -ResourceGroup rg-audioblog
  ./scripts/deploy.ps1 -ResourceGroup rg-audioblog -Location westeurope -WorkerScaleToZero -CosmosFreeTier -GrantMeAccess
#>
param(
    [Parameter(Mandatory)] [string] $ResourceGroup,
    [string] $Location = 'westeurope',
    [string] $AiLocation = 'swedencentral',
    [string] $StaticWebAppLocation = 'westeurope',
    # Use the Cosmos DB free tier instead of serverless (only one free-tier account per subscription).
    [switch] $CosmosFreeTier,
    # Let the worker scale to zero when idle (cheaper, slower first pickup). See README "Cost".
    [switch] $WorkerScaleToZero,
    # Also give your own user data access, so you can run the app locally against the Azure resources.
    [switch] $GrantMeAccess
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent

$parameters = @(
    "location=$Location"
    "aiLocation=$AiLocation"
    "staticWebAppLocation=$StaticWebAppLocation"
    "cosmosFreeTier=$($CosmosFreeTier.IsPresent.ToString().ToLower())"
    "workerScaleToZero=$($WorkerScaleToZero.IsPresent.ToString().ToLower())"
)
if ($GrantMeAccess) {
    $me = az ad signed-in-user show --query id -o tsv
    $parameters += "developerPrincipalId=$me"
}

Write-Host "Resource group $ResourceGroup ($Location)" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

Write-Host '[1/4] Infrastructure: Cosmos DB, Storage, Azure OpenAI, Speech, registry, Static Web App' -ForegroundColor Cyan
$infra = az deployment group create --resource-group $ResourceGroup --name audioblog-infra `
    --template-file "$root/infra/main.bicep" --parameters @parameters deployApps=false `
    --query properties.outputs --output json | ConvertFrom-Json

Write-Host '[2/4] Building container images in Azure Container Registry' -ForegroundColor Cyan
$tag = Get-Date -Format 'yyyyMMdd-HHmmss'
$apiImage = "$($infra.registryServer.value)/audioblog-api:$tag"
$workerImage = "$($infra.registryServer.value)/audioblog-worker:$tag"
az acr build --registry $infra.registryName.value --image "audioblog-api:$tag" --file "$root/src/AudioBlog.Api/Dockerfile" $root --no-logs --output none
az acr build --registry $infra.registryName.value --image "audioblog-worker:$tag" --file "$root/src/AudioBlog.Worker/Dockerfile" $root --no-logs --output none

Write-Host '[3/4] Container Apps: API and worker' -ForegroundColor Cyan
$apps = az deployment group create --resource-group $ResourceGroup --name audioblog-apps `
    --template-file "$root/infra/main.bicep" --parameters @parameters deployApps=true apiImage=$apiImage workerImage=$workerImage `
    --query properties.outputs --output json | ConvertFrom-Json
$apiUrl = $apps.apiUrl.value

Write-Host '[4/4] Frontend: build and upload to Static Web Apps' -ForegroundColor Cyan
Push-Location "$root/web"
try {
    $env:VITE_API_BASE_URL = $apiUrl
    npm ci
    npm run build
}
finally {
    Remove-Item Env:VITE_API_BASE_URL -ErrorAction SilentlyContinue
    Pop-Location
}
# The deployment token is fetched at deploy time and never stored.
$token = az staticwebapp secrets list --name $infra.staticWebAppName.value --resource-group $ResourceGroup --query properties.apiKey -o tsv
npx --yes @azure/static-web-apps-cli@2.1.0 deploy "$root/web/dist" --deployment-token $token --env production

Write-Host ''
Write-Host "Web app: $($infra.webUrl.value)" -ForegroundColor Green
Write-Host "API:     $apiUrl" -ForegroundColor Green
if ($GrantMeAccess) {
    Write-Host ''
    Write-Host 'To run locally against these Azure resources, set (e.g. in your shell or user secrets):' -ForegroundColor Yellow
    Write-Host "  App__AiMode=Azure"
    Write-Host "  AzureOpenAI__Endpoint=$($infra.openAiEndpoint.value)"
    Write-Host "  AzureOpenAI__Deployment=$($infra.chatDeploymentName.value)"
    Write-Host "  Speech__Region=$($infra.speechRegion.value)"
    Write-Host "  Speech__ResourceId=$($infra.speechResourceId.value)"
}
