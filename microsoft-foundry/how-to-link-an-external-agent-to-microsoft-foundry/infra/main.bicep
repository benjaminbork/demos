// ---------------------------------------------------------------------------
// Link an AI agent to Microsoft Foundry: entry point
//
//   browser ──► App Service (FastAPI + Microsoft Agent Framework)  ── your code, your host
//                 ├─ model call ───► Foundry project (gpt-5.4-mini)  ── RBAC: Foundry User
//                 └─ OTel traces ──► Application Insights ◄── connected to Foundry
//
//   modules/agent-registration.bicep registers the agent in the Foundry project as
//   an *external agent* (same id as gen_ai.agent.id in the traces) → Foundry shows
//   the agent and its traces, although it never hosts or calls it.
// ---------------------------------------------------------------------------
targetScope = 'resourceGroup'

@description('Short name used to derive all resource names (azd environment name).')
@minLength(3)
@maxLength(12)
param environmentName string

@description('Azure region for all resources. The model must be available there as GlobalStandard.')
param location string = resourceGroup().location

@description('Object ID of the developer who runs the app locally and views the traces. Defaults to whoever deploys. "" to skip.')
param developerPrincipalId string = deployer().objectId

@description('Chat model for the agent.')
param modelName string = 'gpt-5.4-mini'

@description('Model version.')
param modelVersion string = '2026-03-17'

@description('Model capacity in thousands of tokens per minute.')
param modelCapacity int = 30

@description('Agent name in Foundry = OpenTelemetry agent id (gen_ai.agent.id) the app emits. Letters, digits, - and _.')
param agentName string = 'demo-agent'

@description('Record prompts and replies in the traces. Nice for a demo, think twice in production.')
param captureMessageContent bool = true

var suffix = uniqueString(resourceGroup().id, environmentName)
var tags = {
  'azd-env-name': environmentName
  sample: 'foundry-link-agent'
}

// Names are computed here (not taken from module outputs) so the role
// assignments below can reference the resources as `existing`.
var foundryAccountName = 'external-agent-demo-${suffix}' // also the custom subdomain, so it must be globally unique
var appInsightsName = 'appi-${environmentName}-${suffix}'
var webAppName = 'app-${environmentName}-${suffix}'

var roles = {
  foundryUser: '53ca6127-db72-4b80-b1b0-d745d6d5456d' // Foundry User (formerly Azure AI User)
  monitoringReader: '43d0d8ad-25c7-4714-9337-8ba259a9fe05' // read traces in Foundry
}

// ── 1. Observability: where the agent's traces go ─────────────────────────
module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    name: '${environmentName}-${suffix}'
    location: location
    tags: tags
  }
}

// ── 2. Foundry account + project + model (+ App Insights connection) ───────
module foundry 'modules/foundry.bicep' = {
  name: 'foundry'
  params: {
    accountName: foundryAccountName
    projectName: 'external-agent-demo'
    location: location
    modelName: modelName
    modelVersion: modelVersion
    modelCapacity: modelCapacity
    appInsightsId: monitoring.outputs.appInsightsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    tags: tags
  }
}

// ── 3. The agent: FastAPI + Agent Framework in App Service ─────────────────
module app 'modules/appservice.bicep' = {
  name: 'app'
  params: {
    name: webAppName
    location: location
    serviceName: 'agent'
    appSettings: {
      FOUNDRY_PROJECT_ENDPOINT: foundry.outputs.projectEndpoint
      FOUNDRY_MODEL: foundry.outputs.modelDeploymentName
      AGENT_NAME: agentName
      APPLICATIONINSIGHTS_CONNECTION_STRING: monitoring.outputs.appInsightsConnectionString
      ENABLE_SENSITIVE_DATA: captureMessageContent ? 'true' : 'false'
      OTEL_SERVICE_NAME: agentName
    }
    tags: tags
  }
}

// ── 4. The link: register the agent in the Foundry project ─────────────────
module registration 'modules/agent-registration.bicep' = {
  name: 'agent-registration'
  params: {
    name: 'register-${agentName}-${suffix}'
    location: location
    foundryAccountName: foundry.outputs.accountName
    projectName: foundry.outputs.projectName
    projectEndpoint: foundry.outputs.projectEndpoint
    agentName: agentName
    tags: tags
  }
}

// ── 5. RBAC ────────────────────────────────────────────────────────────────
resource foundryAccount 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
  dependsOn: [
    foundry
  ]
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  dependsOn: [
    monitoring
  ]
}

// The agent's managed identity may call models in the Foundry project. No API key exists
// (disableLocalAuth), so without this role every model call fails with 401/403.
resource agentIsFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: foundryAccount
  name: guid(foundryAccount.id, webAppName, roles.foundryUser)
  properties: {
    principalId: app.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

// You: run the app locally with `az login` (same model calls as the managed identity).
resource developerIsFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  scope: foundryAccount
  name: guid(foundryAccount.id, developerPrincipalId, roles.foundryUser)
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

// You: see the agent's traces in the Foundry portal (they're read from Application Insights).
resource developerReadsTraces 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(developerPrincipalId)) {
  scope: appInsights
  name: guid(appInsights.id, developerPrincipalId, roles.monitoringReader)
  properties: {
    principalId: developerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.monitoringReader)
  }
}

// Outputs → azd stores them in .azure/<env>/.env; a local run reads them
output FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output FOUNDRY_MODEL string = foundry.outputs.modelDeploymentName
output FOUNDRY_ACCOUNT_NAME string = foundry.outputs.accountName
output AGENT_NAME string = registration.outputs.agentName
output AGENT_URL string = app.outputs.url
output APP_NAME string = app.outputs.name
output APPLICATIONINSIGHTS_NAME string = monitoring.outputs.appInsightsName
