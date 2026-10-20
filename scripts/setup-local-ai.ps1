#requires -Version 7
<#
.SYNOPSIS
  Creates only Azure OpenAI + Azure AI Speech and points the LOCAL worker at them,
  so local runs (Cosmos DB emulator + Azurite) produce real narration.

  Writes src/AudioBlog.Worker/appsettings.Local.json (git-ignored, contains no secrets:
  authentication uses your `az login` through DefaultAzureCredential).

.EXAMPLE
  ./scripts/setup-local-ai.ps1
  ./scripts/setup-local-ai.ps1 -ResourceGroup rg-audioblog-ai -Location swedencentral
#>
param(
    [string] $ResourceGroup = 'rg-audioblog-ai',
    [string] $Location = 'swedencentral'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent

$me = az ad signed-in-user show --query id -o tsv
Write-Host "Resource group $ResourceGroup ($Location)" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

Write-Host 'Deploying Azure OpenAI + Speech (a few minutes)...' -ForegroundColor Cyan
$out = az deployment group create --resource-group $ResourceGroup --name audioblog-ai `
    --template-file "$root/infra/ai-only.bicep" --parameters developerPrincipalId=$me `
    --query properties.outputs --output json | ConvertFrom-Json

$settings = [ordered]@{
    App         = @{ AiMode = 'Azure' }
    AzureOpenAI = @{ Endpoint = $out.openAiEndpoint.value; Deployment = $out.chatDeploymentName.value }
    Speech      = @{ Region = $out.speechRegion.value; ResourceId = $out.speechResourceId.value }
}
$file = Join-Path $root 'src/AudioBlog.Worker/appsettings.Local.json'
$settings | ConvertTo-Json -Depth 5 | Set-Content -Path $file -Encoding utf8

Write-Host ''
Write-Host "Wrote $file" -ForegroundColor Green
Write-Host 'Restart the worker (dotnet run --project src/AudioBlog.Worker) to use real AI.' -ForegroundColor Green
Write-Host 'Role assignments can take a few minutes to become active; if the first article fails with' -ForegroundColor Yellow
Write-Host '"rejected the request", wait a moment and press Retry.' -ForegroundColor Yellow
Write-Host "To remove everything again: az group delete --name $ResourceGroup" -ForegroundColor DarkGray
