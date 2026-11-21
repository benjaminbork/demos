// ---------------------------------------------------------------------------
// App Service (Linux, Python) for the FastAPI demo agent
//
// System-assigned managed identity = the agent's identity in Entra ID.
// It gets "Foundry User" on the Foundry account in main.bicep, so the code
// needs no API key (DefaultAzureCredential picks the identity up).
// ---------------------------------------------------------------------------

@description('Web app name (also the default hostname).')
param name string

@description('Azure region.')
param location string

@description('App Service plan SKU. B1 is enough for the demo.')
param skuName string = 'B1'

@description('Python version of the App Service runtime.')
param pythonVersion string = '3.13'

@description('App settings (environment variables of the app).')
param appSettings object

@description('azd service name, used to find the app on `azd deploy`.')
param serviceName string

@description('Tags applied to the resources.')
param tags object = {}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${name}'
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: skuName
  }
  properties: {
    reserved: true // Linux
  }
}

resource app 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': serviceName })
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'PYTHON|${pythonVersion}'
      // One worker on purpose: chat sessions live in memory (demo only)
      appCommandLine: 'python -m uvicorn app:app --host 0.0.0.0 --port 8000'
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      healthCheckPath: '/health'
    }
  }

  resource settings 'config' = {
    name: 'appsettings'
    properties: union(appSettings, {
      SCM_DO_BUILD_DURING_DEPLOYMENT: 'true' // Oryx runs pip install -r requirements.txt on deploy
      WEBSITES_PORT: '8000'
    })
  }

  // No basic auth (FTP / Kudu): azd deploys with your Entra ID token
  resource ftp 'basicPublishingCredentialsPolicies' = {
    name: 'ftp'
    properties: {
      allow: false
    }
  }

  resource scm 'basicPublishingCredentialsPolicies' = {
    name: 'scm'
    properties: {
      allow: false
    }
  }
}

output name string = app.name
output url string = 'https://${app.properties.defaultHostName}'
output principalId string = app.identity.principalId
