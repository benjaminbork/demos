"""Demo agent: Microsoft Agent Framework in FastAPI on App Service, linked to Microsoft Foundry.

Nothing special about the agent itself, it's a plain chatbot. The point is the wiring:

* Model:  FoundryChatClient calls the model deployed in the Foundry project.
          Keyless: DefaultAzureCredential = the App Service managed identity in
          Azure (role "Foundry User"), your `az login` locally.
* Traces: Agent Framework emits OpenTelemetry spans (invoke_agent, chat, ...)
          and Azure Monitor exports them to the Application Insights resource
          that is connected to the Foundry project.
* Link:   every invoke_agent span carries gen_ai.agent.id = AGENT_NAME.
          infra/modules/agent-registration.bicep registers an external agent with
          that same id in Foundry (during `azd up`), so the Foundry portal lists
          the agent and its traces.

    GET  /                                     -> tiny chat page
    POST /chat {"message": "..."}              -> starts a chat
    POST /chat {"sessionId": "...", "message"} -> continues it
"""

import os
from contextlib import AsyncExitStack, asynccontextmanager
from pathlib import Path

from agent_framework import Agent, AgentSession
from agent_framework.foundry import FoundryChatClient
from agent_framework.observability import create_resource, enable_instrumentation
from azure.identity.aio import DefaultAzureCredential
from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel

# Must match the name of the external agent in Foundry (see infra/modules/agent-registration.bicep)
AGENT_NAME = os.environ.get("AGENT_NAME", "demo-agent")

INSTRUCTIONS = """You are a friendly demo chatbot on a tech blog.
Keep answers short (max. 5 sentences) and reply in the language of the user."""

# ── Telemetry: send Agent Framework spans to Application Insights ───────────
# Runs once at import, before the agent exists. Without a connection string
# (e.g. a quick local run) the app works the same, just without traces.
if os.environ.get("APPLICATIONINSIGHTS_CONNECTION_STRING"):
    from azure.monitor.opentelemetry import configure_azure_monitor

    configure_azure_monitor(resource=create_resource())  # reads the connection string from the env
    enable_instrumentation()  # ENABLE_SENSITIVE_DATA=true also records prompts and replies


class ChatRequest(BaseModel):
    message: str
    sessionId: str | None = None  # continue an existing chat


class ChatResponse(BaseModel):
    sessionId: str
    reply: str


sessions: dict[str, AgentSession] = {}  # in-memory: one worker, demo only


@asynccontextmanager
async def lifespan(app: FastAPI):
    async with AsyncExitStack() as stack:
        credential = await stack.enter_async_context(DefaultAzureCredential())
        app.state.agent = await stack.enter_async_context(
            Agent(
                client=FoundryChatClient(
                    project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
                    model=os.environ.get("FOUNDRY_MODEL", "gpt-5.4-mini"),
                    credential=credential,
                ),
                id=AGENT_NAME,  # → gen_ai.agent.id on every invoke_agent span = the link to Foundry
                name=AGENT_NAME,
                instructions=INSTRUCTIONS,
            )
        )
        yield


app = FastAPI(title="Demo chatbot linked to Microsoft Foundry", lifespan=lifespan)


@app.get("/", include_in_schema=False)
async def index() -> FileResponse:
    return FileResponse(Path(__file__).parent / "static" / "index.html")


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/chat")
async def chat(request: ChatRequest) -> ChatResponse:
    agent: Agent = app.state.agent

    if request.sessionId:
        session = sessions.get(request.sessionId)
        if session is None:
            raise HTTPException(404, "Unknown sessionId, start a new chat without one")
    else:
        session = agent.create_session()
        sessions[session.session_id] = session

    response = await agent.run(request.message, session=session)
    return ChatResponse(sessionId=session.session_id, reply=response.text)
