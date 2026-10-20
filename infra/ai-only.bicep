// Only the AI services (Azure OpenAI + Speech), for running the app locally against the emulators
// with real narration. Deployed by scripts/setup-local-ai.ps1. No idle cost: both are pay per use.
targetScope = 'resourceGroup'

param location string = resourceGroup().location
@maxLength(12)
param appName string = 'audioblog'
@description('Object id of your user (az ad signed-in-user show).')
param developerPrincipalId string

param chatModelName string = 'gpt-5.4-mini'
param chatModelVersion string = '2026-03-17'
param chatCapacity int = 50
@allowed([ 'S0', 'F0' ])
param speechSku string = 'S0'

var suffix = uniqueString(resourceGroup().id)

module ai 'modules/ai.bicep' = {
  name: 'ai'
  params: {
    location: location
    openAiName: 'oai-${appName}-${suffix}'
    speechName: 'spch-${appName}-${suffix}'
    chatDeploymentName: chatModelName
    chatModelName: chatModelName
    chatModelVersion: chatModelVersion
    chatCapacity: chatCapacity
    speechSku: speechSku
    userPrincipals: [ { id: developerPrincipalId, type: 'User' } ]
  }
}

output openAiEndpoint string = ai.outputs.openAiEndpoint
output chatDeploymentName string = ai.outputs.chatDeploymentName
output speechRegion string = ai.outputs.speechRegion
output speechResourceId string = ai.outputs.speechResourceId
