// ---------------------------------------------------------------------------
// Microsoft Foundry: account + project + model deployment + Application Insights connection
//
// The agent itself is NOT created here. It runs in App Service (your code) and
// is only *linked* to the project by agent-registration.bicep.
// ---------------------------------------------------------------------------

@description('Foundry account name (also used as custom subdomain).')
@minLength(2)
@maxLength(64)
param accountName string

@description('Foundry project name.')
param projectName string

@description('Azure region.')
param location string

@description('Chat model name.')
param modelName string

@description('Chat model version.')
param modelVersion string

@description('Capacity in thousands of tokens per minute.')
param modelCapacity int

@description('Resource ID of the Application Insights resource that receives the agent traces.')
param appInsightsId string

@description('Connection string of that Application Insights resource.')
@secure()
param appInsightsConnectionString string

@description('Tags applied to the resources.')
param tags object = {}

// Foundry account = an AI Services account that can hold projects
resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowProjectManagement: true // makes this a Foundry resource that can hold projects
    customSubDomainName: accountName
    disableLocalAuth: true // Entra ID only, no API keys -> the agent needs RBAC
    publicNetworkAccess: 'Enabled'
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'External agent demo'
    description: 'A chatbot that runs in App Service and is linked to Foundry as an external agent'
  }
}

// The account accepts one write at a time ("RequestConflict: Another operation is in
// progress"), so project → model → connection are created one after another.
resource model 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: modelName
  dependsOn: [
    project
  ]
  sku: {
    name: 'GlobalStandard'
    capacity: modelCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: modelName
      version: modelVersion
    }
  }
}

// Same as "Agents > Traces > Connect" in the portal: Foundry reads the traces
// of all agents (also external ones) from this Application Insights resource.
resource appInsightsConnection 'Microsoft.CognitiveServices/accounts/connections@2025-06-01' = {
  parent: account
  name: 'appinsights'
  dependsOn: [
    model
  ]
  properties: {
    category: 'AppInsights'
    target: appInsightsId
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: {
      key: appInsightsConnectionString
    }
    metadata: {
      ApiType: 'Azure'
      ResourceId: appInsightsId
    }
  }
}

output accountName string = account.name
output projectName string = project.name
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output modelDeploymentName string = model.name
