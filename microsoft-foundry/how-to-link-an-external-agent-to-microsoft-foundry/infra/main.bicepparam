using 'main.bicep'

// azd passes its environment name (AZURE_ENV_NAME); keep it 3-12 chars, e.g. `azd env new linkagent`.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'linkagent')

// Optional overrides
// param location = 'swedencentral'
// param modelName = 'gpt-5.4-mini'
// param modelVersion = '2026-03-17'
// param agentName = 'demo-agent'
// param captureMessageContent = false
