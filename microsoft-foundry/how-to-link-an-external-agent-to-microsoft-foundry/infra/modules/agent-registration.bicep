// ---------------------------------------------------------------------------
// Link the agent to Foundry: register it as an *external agent*
//
// Foundry agents are data-plane objects (POST {project}/agents/{name}/versions),
// there is no ARM resource type for them. A deployment script makes that call
// during `azd up`, so the link lives in Bicep like everything else.
//
// Registration is metadata only: Foundry never hosts or calls the agent. It shows
// the traces whose gen_ai.agent.id equals otel_agent_id (= AGENT_NAME in the app).
//
// Who does what:
//   • the script runs as its own user-assigned identity (Foundry User on the project)
//   • you (the deployer) need Owner/User Access Administrator, which the other
//     role assignments in main.bicep need anyway (includes Managed Identity Operator)
//   • the script service creates a temporary storage account + container instance
//     in the resource group and deletes them when the script succeeds
// ---------------------------------------------------------------------------

@description('Base name for the script and its identity.')
param name string

@description('Azure region.')
param location string

@description('Foundry account that holds the project.')
param foundryAccountName string

@description('Foundry project the agent is registered in.')
param projectName string

@description('Project endpoint, e.g. https://<account>.services.ai.azure.com/api/projects/<project>.')
param projectEndpoint string

@description('Agent name in Foundry = gen_ai.agent.id the app emits. Letters, digits, - and _.')
param agentName string

@description('Description shown in the Foundry portal.')
param agentDescription string = 'Chatbot running in App Service, linked to Foundry as an external agent'

@description('Tags applied to the resources.')
param tags object = {}

var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d' // Foundry User (formerly Azure AI User)

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName

  resource project 'projects' existing = {
    name: projectName
  }
}

// Identity the script signs in with (deployment scripts only accept user-assigned identities)
resource scriptIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}'
  location: location
  tags: tags
}

// Registering an agent needs Foundry User on the project, nothing more
resource scriptIsFoundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: account::project
  name: guid(account::project.id, scriptIdentity.id, foundryUserRoleId)
  properties: {
    principalId: scriptIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

// Reruns only when its properties change (e.g. another agent name), not on every `azd up`
resource registerAgent 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: name
  location: location
  tags: tags
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${scriptIdentity.id}': {}
    }
  }
  properties: {
    azCliVersion: '2.80.0'
    timeout: 'PT20M'
    retentionInterval: 'PT1H'
    cleanupPreference: 'OnSuccess'
    environmentVariables: [
      { name: 'PROJECT_ENDPOINT', value: projectEndpoint }
      { name: 'AGENT_NAME', value: agentName }
      { name: 'AGENT_DESCRIPTION', value: agentDescription }
    ]
    // Same call as project.agents.create_version(..., definition=ExternalAgentDefinition(otel_agent_id=...))
    // The loop waits for the new role assignment to propagate (can take a few minutes).
    scriptContent: '''
      set -euo pipefail
      body=$(jq -n --arg d "$AGENT_DESCRIPTION" --arg id "$AGENT_NAME" \
        '{description: $d, definition: {kind: "external", otel_agent_id: $id}}')

      for attempt in $(seq 1 20); do
        if az rest --method POST \
             --url "$PROJECT_ENDPOINT/agents/$AGENT_NAME/versions?api-version=v1" \
             --resource "https://ai.azure.com" \
             --headers "Content-Type=application/json" \
             --body "$body" > result.json; then
          jq -c '{agentName: .name, version: .version}' result.json > "$AZ_SCRIPTS_OUTPUT_PATH"
          echo "Registered external agent $AGENT_NAME"
          exit 0
        fi
        echo "Attempt $attempt failed (role assignment not active yet?), retrying in 30 s"
        sleep 30
      done
      echo "Could not register $AGENT_NAME" >&2
      exit 1
    '''
  }
  dependsOn: [
    scriptIsFoundryUser
  ]
}

output agentName string = registerAgent.properties.outputs.agentName
output identityPrincipalId string = scriptIdentity.properties.principalId
