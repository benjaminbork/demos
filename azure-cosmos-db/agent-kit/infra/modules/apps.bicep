// Container Apps: the API (scales to zero) and the background worker (one small replica).

param location string
param environmentName string
param logAnalyticsName string
param apiName string
param workerName string
param apiImage string
param workerImage string
param registryServer string
param apiIdentityId string
param apiIdentityClientId string
param workerIdentityId string
param workerIdentityClientId string
param allowedOrigins array
param cosmosEndpoint string
param blobEndpoint string
param openAiEndpoint string
param chatDeploymentName string
param speechRegion string
param speechResourceId string
@description('false: one worker replica always runs (simplest, ~4 €/month at the idle rate). true: the worker scales to zero and is woken over HTTP by the API, by itself while work is pending, and hourly by a cron rule.')
param workerScaleToZero bool

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json('0.2') } // hobby project: cap log ingestion cost
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
  }
}

// Internal (environment-only) address of the worker, known before it exists.
var workerInternalUrl = 'https://${workerName}.internal.${environment.properties.defaultDomain}'

var corsEnv = [for (origin, i) in allowedOrigins: { name: 'Cors__AllowedOrigins__${i}', value: origin }]

var commonEnv = [
  { name: 'Cosmos__Endpoint', value: cosmosEndpoint }
  { name: 'Storage__BlobEndpoint', value: blobEndpoint }
  { name: 'Logging__Console__FormatterName', value: 'simple' }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentityId}': {} } }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [ { server: registryServer, identity: apiIdentityId } ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(commonEnv, corsEnv, [
            { name: 'AZURE_CLIENT_ID', value: apiIdentityClientId }
          ], workerScaleToZero ? [ { name: 'Worker__WakeUrl', value: '${workerInternalUrl}/wake' } ] : [])
          probes: [
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: {
        minReplicas: 0 // scale to zero when nobody uses the app
        maxReplicas: 2
        rules: [ { name: 'http', http: { metadata: { concurrentRequests: '30' } } } ]
      }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: workerName
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentityId}': {} } }
  properties: {
    environmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      // Only needed to be woken up; reachable from inside the environment only.
      ingress: workerScaleToZero ? { external: false, targetPort: 8080, transport: 'auto', allowInsecure: false } : null
      registries: [ { server: registryServer, identity: workerIdentityId } ]
    }
    template: {
      // Container Apps sends SIGTERM and waits this long; the worker hands in-flight articles back.
      terminationGracePeriodSeconds: 30
      containers: [
        {
          name: 'worker'
          image: workerImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(commonEnv, [
            { name: 'AZURE_CLIENT_ID', value: workerIdentityClientId }
            { name: 'AzureOpenAI__Endpoint', value: openAiEndpoint }
            { name: 'AzureOpenAI__Deployment', value: chatDeploymentName }
            { name: 'Speech__Region', value: speechRegion }
            { name: 'Speech__ResourceId', value: speechResourceId }
          ], workerScaleToZero ? [ { name: 'Worker__SelfUrl', value: workerInternalUrl } ] : [])
          probes: workerScaleToZero ? [ { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 30 } ] : []
        }
      ]
      scale: {
        minReplicas: workerScaleToZero ? 0 : 1
        // More replicas are safe (leases + ETags), but one is plenty for a single user.
        maxReplicas: 1
        rules: workerScaleToZero ? [
          { name: 'wake-on-http', http: { metadata: { concurrentRequests: '10' } } }
          // Safety net: run 10 minutes every hour even if every wake-up call was lost,
          // so scheduled retries and orphaned leases are always picked up eventually.
          {
            name: 'hourly-sweep'
            custom: {
              type: 'cron'
              metadata: { timezone: 'Etc/UTC', start: '0 * * * *', end: '10 * * * *', desiredReplicas: '1' }
            }
          }
        ] : []
      }
    }
  }
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
