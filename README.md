# How to link an AI agent to Microsoft Foundry 🔗

You built an agent with the **Microsoft Agent Framework**, and it runs on **your** host (here: a FastAPI app in **Azure App Service**). This demo shows how to make it show up in **Microsoft Foundry** anyway, with its traces, without moving it into Foundry

The agent is a plain chatbot (nothing special, it's a demo). The interesting part is the wiring:

```
 browser ──POST /chat──► App Service (FastAPI + Agent Framework)           your code, your host
                           │  managed identity ── RBAC: Foundry User ──┐
                           ├─ model call ──────────────────────────────┴─► Foundry project ─ gpt-5.4-mini
                           └─ OpenTelemetry spans ──► Application Insights ◄── connected to the Foundry account
                                gen_ai.agent.id = "demo-agent"                 │
                                                                                 ▼
 Bicep deployment script ──register external agent "demo-agent"──► Foundry: Build > Agents > demo-agent > Traces
```

🖼️ Architecture diagram with the official Azure icons: [`docs/architecture.excalidraw`](docs/architecture.excalidraw) (open it in [excalidraw.com](https://excalidraw.com) or the VS Code Excalidraw extension)

| Piece | What it is | File |
|---|---|---|
| 🏗️ **Bicep** | Foundry account + project, model deployment, Application Insights (connected to Foundry), App Service, RBAC | [`infra/`](infra) |
| 🐍 **App Service with FastAPI** | `Agent` + `FoundryChatClient`, `/chat` API and a tiny chat page | [`src/app.py`](src/app.py) (~100 lines) |
| 🔗 **The link, in Bicep** | a deployment script registers the app as an **external agent** in the Foundry project during `azd up` | [`infra/modules/agent-registration.bicep`](infra/modules/agent-registration.bicep) |
| 🔐 **RBAC for the agent** | the App Service managed identity gets **Foundry User**, no API keys anywhere | [`infra/main.bicep`](infra/main.bicep) |
| 🧠 **AI model** | `gpt-5.4-mini`, GlobalStandard deployment in the Foundry account | [`infra/modules/foundry.bicep`](infra/modules/foundry.bicep) |

## What "link" means 🤔

Foundry knows three kinds of code agents:

| | Runs where | Foundry hosts it | Foundry calls it |
|---|---|---|---|
| **Hosted agent** | Foundry (container) | ✅ | ✅ |
| **Custom agent** (Control Plane) | anywhere, behind an AI Gateway (API Management) | ❌ | ✅ via the gateway. No new registrations, retired on May 7, 2027 |
| **External agent** ← this demo | anywhere | ❌ | ❌ |

An [external agent](https://learn.microsoft.com/azure/foundry/agents/how-to/register-external-agent) is just a **record** in the Foundry project: a name plus an **OpenTelemetry agent id**. Foundry doesn't host, proxy or call your agent. It reads the traces from the Application Insights resource connected to the project and shows the ones whose `gen_ai.agent.id` matches. So linking needs exactly three things:

1. ✍️ Your agent emits OpenTelemetry spans with a **stable** `gen_ai.agent.id`
2. 📈 The spans go to the **Application Insights resource connected to Foundry**
3. 📝 You register an external agent with the **same id**

## The three steps in code 👩‍💻

**1. A stable agent id** ([`src/app.py`](src/app.py)). Agent Framework puts `Agent(id=...)` on every `invoke_agent` span as `gen_ai.agent.id`. Without `id` every `Agent` instance gets a random UUID, so nothing would match the registration after the next restart:

```python
Agent(
    client=FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ.get("FOUNDRY_MODEL", "gpt-5.4-mini"),
        credential=credential,  # DefaultAzureCredential = App Service managed identity
    ),
    id=AGENT_NAME,  # → gen_ai.agent.id = the link to Foundry
    name=AGENT_NAME,
    instructions=INSTRUCTIONS,
)
```

**2. Traces to Application Insights** ([`src/app.py`](src/app.py)). Two calls at startup. `APPLICATIONINSIGHTS_CONNECTION_STRING` is an app setting that Bicep fills with the Application Insights resource it also connects to Foundry:

```python
configure_azure_monitor(resource=create_resource())  # azure-monitor-opentelemetry
enable_instrumentation()  # Agent Framework spans: invoke_agent, chat, execute_tool
```

**3. Register the external agent** ([`infra/modules/agent-registration.bicep`](infra/modules/agent-registration.bicep)). Foundry agents are data-plane objects, there is no ARM resource type for them. So Bicep runs a [deployment script](https://learn.microsoft.com/azure/azure-resource-manager/bicep/deployment-script-bicep) that makes the one REST call, the same one `project.agents.create_version(..., definition=ExternalAgentDefinition(...))` makes:

```bash
az rest --method POST \
  --url "$PROJECT_ENDPOINT/agents/demo-agent/versions?api-version=v1" \
  --resource "https://ai.azure.com" \
  --body '{"definition": {"kind": "external", "otel_agent_id": "demo-agent"}}'
```

The script runs as its own user-assigned identity with **Foundry User** on the project and retries while that role assignment propagates. It reruns only when its inputs change (e.g. another `agentName`), not on every `azd up`. `azd down` removes the registration together with the Foundry account. The app keeps running either way

## RBAC 🔐

The Foundry account has `disableLocalAuth: true`, so there are no API keys. Everything is Entra ID + roles. Read each row as *who* gets *which role* on *which resource* ([`infra/main.bicep`](infra/main.bicep) section 5, [`agent-registration.bicep`](infra/modules/agent-registration.bicep)):

| Who | Role | Scope | Why |
|---|---|---|---|
| App Service managed identity (the agent) | **Foundry User** | Foundry account | call the model through the project endpoint |
| Deployment script identity | **Foundry User** | Foundry project | register the external agent |
| You (the deployer) | **Foundry User** | Foundry account | run the app locally |
| You (the deployer) | **Monitoring Reader** | Application Insights | see the traces in the Foundry portal |

Deploying needs **Owner** (or User Access Administrator + Contributor) on the resource group, because Bicep creates role assignments and hands the script its identity

> **Foundry User** was called **Azure AI User** until recently, same role id `53ca6127-db72-4b80-b1b0-d745d6d5456d`

## Run 🚀

You need the [Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd), the Azure CLI and Python 3.11+. From this folder:

```bash
azd auth login
```

```bash
az login
```

The demo lives in the resource group `rg-external-agent-demo` (azd would otherwise name it `rg-<env name>`):

```bash
azd env new linkagent --location swedencentral
```

```bash
az group create -n rg-external-agent-demo -l swedencentral --tags azd-env-name=linkagent
```

```bash
azd env set AZURE_RESOURCE_GROUP rg-external-agent-demo
```

```bash
azd up
```

`azd up` does everything in order:

1. **provision**: deploys [`infra/main.bicep`](infra/main.bicep), including the agent registration (pick a region with `gpt-5.4-mini` GlobalStandard quota, e.g. `swedencentral`). The first run takes a few extra minutes: the deployment script starts a container and waits for its role assignment
2. **deploy**: zips [`src/`](src) to App Service, Oryx installs `requirements.txt`

Then open the chat page:

```bash
azd env get-value AGENT_URL
```

Or use the API:

```bash
curl -sS -X POST "$(azd env get-value AGENT_URL)/chat" -H "Content-Type: application/json" -d '{"message": "Translate into German: The weather is nice today."}'
```

The chatbot replies with something like `Das Wetter ist heute schön.` Continue the same chat with the `sessionId` from the response: `{"sessionId": "...", "message": "..."}`

## See the agent in Foundry 👀

1. Open the [Foundry portal](https://ai.azure.com) and your project (`external-agent-demo`)
2. **Build > Agents**: `demo-agent` is listed as an external agent
3. Select it, open **Traces**: one trace per chat message, with the `invoke_agent demo-agent` span and the `chat gpt-5.4-mini` span below it (tokens, latency and, because `captureMessageContent` is on, the prompt and the reply)

Traces take **2-5 minutes** to arrive. Nothing there? Check Application Insights directly, under **Logs**:

```kusto
union requests, dependencies
| where timestamp > ago(30m)
| where tostring(customDimensions['gen_ai.agent.id']) == "demo-agent"
| project timestamp, name, operation_Id
| order by timestamp desc
```

- Rows here but nothing in Foundry → the agent name and `gen_ai.agent.id` differ, or Foundry is connected to another Application Insights resource
- No rows → the app sends no traces. Check that `APPLICATIONINSIGHTS_CONNECTION_STRING` is set on the web app and look at the log stream (`az webapp log tail`)

Because the link is only telemetry, the traces also unlock [trace-based evaluations](https://learn.microsoft.com/azure/foundry/observability/how-to/cloud-evaluation-deployed-interactions) for the agent in Foundry

## Run locally 🖥️

After `azd up` your user has the roles, so the app works on your machine with your `az login` and sends traces to the same Application Insights:

```bash
cd src && python -m venv .venv && source .venv/bin/activate && pip install -r requirements.txt
```

```bash
set -a && eval "$(azd env get-values)" && set +a && export APPLICATIONINSIGHTS_CONNECTION_STRING="$(az monitor app-insights component show -a "$APPLICATIONINSIGHTS_NAME" -g "$AZURE_RESOURCE_GROUP" --query connectionString -o tsv)" && uvicorn app:app --port 8000
```

Local runs show up as the same agent in Foundry, because the id is the same

## Clean up 🧹

```bash
azd down --purge
```

`--purge` also purges the soft-deleted Foundry account, so you can reuse the name

## What's where 🗂️

```
foundry-link-agent/
├── azure.yaml                     azd: infra + App Service service
├── docs/
│   └── architecture.excalidraw    architecture diagram (official Azure icons)
├── infra/
│   ├── main.bicep                 wiring + RBAC + outputs for azd
│   ├── main.bicepparam
│   └── modules/
│       ├── foundry.bicep              account, project, model, App Insights connection
│       ├── monitoring.bicep           Log Analytics + Application Insights
│       ├── appservice.bicep           Linux plan + Python web app (managed identity, no basic auth)
│       └── agent-registration.bicep   the link: deployment script registers the external agent
└── src/
    ├── app.py                     FastAPI + Agent Framework + OpenTelemetry
    ├── requirements.txt
    └── static/index.html          chat page
```

## Limitations ⚠️

| Limitation | Way out |
|---|---|
| `/chat` is public and unauthenticated | App Service authentication (Easy Auth with Entra ID) or API Management in front |
| Sessions live in memory (1 worker, 1 instance) and are lost on restart | Persist `AgentSession`, e.g. in Cosmos DB (see [cosmos-chat-history-memory](../cosmos-chat-history-memory/)) |
| Prompts and replies are recorded in the traces | `param captureMessageContent = false` in [`main.bicepparam`](infra/main.bicepparam) |
| Foundry doesn't route traffic to an external agent, so no gateway blocking, no red teaming, no human evaluation | Use a hosted agent if you want Foundry to run and call it |
| Application Insights ingestion uses the connection string (instrumentation key) | [Entra ID auth for ingestion](https://learn.microsoft.com/azure/azure-monitor/app/azure-ad-authentication) with the **Monitoring Metrics Publisher** role |

## Status 🧪

Deployed with `azd up` to Sweden Central into `rg-external-agent-demo` (Nov 20, 2026, azd 1.23, Bicep 0.48.1) and checked end to end:
- **Bicep:** `az bicep build` and `az bicep lint` pass with no warnings. All resources and the role assignments deploy
- **Model call:** the deployed chatbot answers through its managed identity (no keys). `Translate into German: The weather is nice today.` → `Das Wetter ist heute schön.`, and a second message in the same session sees the first one
- **Link:** the deployment script in [`agent-registration.bicep`](infra/modules/agent-registration.bicep) succeeds and the project `external-agent-demo` returns the agent `demo-agent` with `{"kind": "external", "otel_agent_id": "demo-agent"}`. Its temporary storage account and container are removed afterwards
- **Traces:** Application Insights receives one `invoke_agent demo-agent` span per message with `gen_ai.agent.id = demo-agent`, plus `chat gpt-5.4-mini` below it with the token usage (50 in / 10 out for the example above)
- **Not checked:** the agent's **Traces** tab in the Foundry portal itself (UI only). Both sides it joins, the registration and the spans, are verified above

Found and fixed while deploying:
- The Foundry project, model deployment and App Insights connection are all children of the account, which accepts one write at a time (`RequestConflict: Another operation is in progress`). [`foundry.bicep`](infra/modules/foundry.bicep) now creates them one after another

Known quirk on the very first `azd up`: App Service starts the container before any code exists, and it crashes (`No module named uvicorn`). After the Oryx build the app starts fine within a few minutes, but azd 1.23 kept waiting until its 20-minute deploy timeout. If that happens, check `/health` and run `azd deploy` again. Later runs deploy in about a minute
