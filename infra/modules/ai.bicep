// Azure OpenAI (text rewrite) and Azure AI Speech (neural TTS). Both pay-per-use (no idle cost),
// both with local (key) auth disabled and a custom subdomain, which Entra ID auth requires.

param location string
param openAiName string
param speechName string
param chatDeploymentName string
param chatModelName string
param chatModelVersion string
@description('Thousands of tokens per minute for the GlobalStandard deployment.')
param chatCapacity int
@allowed([ 'S0', 'F0' ])
param speechSku string
@description('Principals ({ id, type }) allowed to call both services.')
param userPrincipals array

resource openAi 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiName
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: openAiName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource chat 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAi
  name: chatDeploymentName
  sku: { name: 'GlobalStandard', capacity: chatCapacity }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
}

resource speech 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: speechName
  location: location
  kind: 'SpeechServices'
  sku: { name: speechSku }
  properties: {
    customSubDomainName: speechName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

var openAiUser = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' // Cognitive Services OpenAI User
var speechUser = 'f2dc8367-1007-4938-bd23-fe263f013447' // Cognitive Services Speech User

resource openAiRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in userPrincipals: {
  scope: openAi
  name: guid(openAi.id, principal.id, openAiUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUser)
    principalId: principal.id
    principalType: principal.type
  }
}]

resource speechRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for principal in userPrincipals: {
  scope: speech
  name: guid(speech.id, principal.id, speechUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', speechUser)
    principalId: principal.id
    principalType: principal.type
  }
}]

output openAiEndpoint string = openAi.properties.endpoint
output chatDeploymentName string = chat.name
output speechResourceId string = speech.id
output speechRegion string = speech.location
