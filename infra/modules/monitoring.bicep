// ---------------------------------------------------------------------------
// Log Analytics + Application Insights
//
// The agent sends its OpenTelemetry traces here. The same Application Insights
// resource is connected to the Foundry account (see foundry.bicep), so Foundry
// can show the traces under the linked agent.
// ---------------------------------------------------------------------------

@description('Base name for the resources.')
param name string

@description('Azure region.')
param location string

@description('Tags applied to the resources.')
param tags object = {}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
