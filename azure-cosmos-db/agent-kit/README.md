# Audio Blog: a test of the Azure Cosmos DB Agent Kit

> **This repository is a test of the [Azure Cosmos DB Agent Kit](https://github.com/AzureCosmosDB/cosmosdb-agent-kit).**
> A coding agent (Claude Code) built a small real-world app on Azure Cosmos DB, first **without** the kit, then reviewed and improved it **with** the kit's `cosmosdb-best-practices` skill. This README follows the steps of the test and records what happened at each one.

![Audio Blog UI](docs/screenshot.jpg)

## Test status at a glance

| # | Step | Status | Result |
|---|---|---|---|
| 1 | Pick a small real-world app where Cosmos DB fits | ✅ | **Audio Blog**: turns an article URL into an MP3 |
| 2 | Write your own prompt for a coding agent | ✅ | [One prompt](#step-2-the-prompt), written the way I'd normally write one |
| 3 | First attempt without Azure MCP tools or agent kit skills | ✅ | Plain Claude Code, no Azure MCP server, no Cosmos DB skill |
| 4 | Let the agent generate the app | ✅ | Complete solution in one go: API, worker, frontend, Bicep, tests |
| 5 | Install dependencies, build, run tests, start the app | ✅ | Built, 33/33 tests passed, ran locally end to end |
| 6 | Record whether it built and ran, plus errors and confusing setup | ✅ | [Two follow-ups needed](#step-6-what-worked-and-what-didnt), both about AI and Azure sign-in, none about Cosmos DB |
| 7 | Enable or install the agent kit skills | ✅ | `cosmosdb-best-practices` installed into `.claude/skills` |
| 8 | Ask the agent to review for Cosmos DB best practices | ✅ | [5 main suggestions + 4 minor](#step-8-what-the-cosmos-db-review-suggested) |
| 9 | Measure the RU effect of the suggested query changes | ✅ | [1 / 5 / 10 physical partitions, 1,000 to 100,000 articles](#step-9-measuring-the-ru-effect-of-the-review) |

---

## How the run was set up

| Item | Setup |
|---|---|
| **Coding agent** | Claude Code, desktop app, Windows 11 |
| **Round 1 tools** | Claude Code's built-in tools only. **No** Azure MCP server, **no** Cosmos DB agent kit skill, no other Cosmos DB plugin. |
| **Round 2 tools** | Same agent plus the agent kit's `cosmosdb-best-practices` skill. A **fresh** session, so it has no memory of round 1. |
| **Cosmos DB** | Local **Cosmos DB emulator (vNext, Linux, Docker)**. No Azure Cosmos DB account was used. |
| **Blob Storage** | Local **Azurite** emulator (Docker) |
| **AI models** | Azure OpenAI (`gpt-5.4-mini`) and Azure AI Speech, **in Azure**, deployed with a small AI-only Bicep. There is no local stand-in for them. |
| **App processes** | API, worker and frontend run locally with `dotnet run` and `npm run dev` |
| **Machine prerequisites** | .NET 10 SDK, Node.js 22+, Docker Desktop, Azure CLI (`az login`), PowerShell 7 |

The full Azure deployment (`infra/main.bicep`, `scripts/deploy.ps1`, with Container Apps, Static Web Apps and a real serverless Cosmos DB account) was generated, but it has **not** been deployed in this test. The app ran against the Cosmos DB emulator; only the RU measurements in [Step 9](#step-9-measuring-the-ru-effect-of-the-review) used a real Azure Cosmos DB account, created just for the test.

---

## Step 1: The app

**Audio Blog.** Paste an article URL, click **Transform**, and get back an MP3 of the article, rewritten so it sounds natural when read aloud.

Why it's a fair test for Cosmos DB:

- **Small enough** to build in one prompt.
- **Cosmos DB is the only database**, so it has to hold the article list, processing state, intermediate text and the work queue.
- **Hard reliability requirements:** async processing, crash safety, no duplicates, retries, resume from the failed step. That forces real design decisions (data model, partition key, concurrency control, scheduling), not just CRUD.
- **A cost limit** of a few euros a month, so RU cost and throughput mode matter.

| Part | Technology |
|---|---|
| Backend | .NET 10: ASP.NET Core minimal API plus a separate background worker |
| Frontend | React, Vite, TypeScript, Material UI |
| Database | Azure Cosmos DB for NoSQL |
| AI | Azure OpenAI rewrites the text into spoken prose; Azure AI Speech produces the neural TTS |
| Files | Azure Blob Storage, private, served through short-lived SAS URLs |
| Hosting | Azure Container Apps (API, worker), Azure Static Web Apps (frontend), Bicep |
| Auth to Azure | Managed identities and `DefaultAzureCredential` only; key-based auth disabled everywhere |

## Step 2: The prompt

This is the only prompt used to generate the app. It has the level of detail I'd normally give a coding agent: fixed tech stack, user flow, pipeline, reliability and cost requirements. It does **not** say how to model the data in Cosmos DB; that was left to the agent.

<details>
<summary><b>Show the prompt</b></summary>

```markdown
# Build: "Audio Blog" – turn any article URL into an MP3

## Goal
A small web app where I paste an article URL, click "Transform", and get back
a listenable MP3 of that article, rewritten so it sounds natural when spoken.

## Tech stack (fixed)
- Backend: .NET (latest LTS), ASP.NET Core minimal API + a separate background worker service
- Frontend: React + Vite + TypeScript, Material UI, kept simple
- Database: Azure Cosmos DB for NoSQL – the ONLY database
- AI: Azure OpenAI (text rewrite), Azure AI Speech (neural TTS)
- File storage: Azure Blob Storage for the MP3 files
- Hosting: API + worker on Azure Container Apps, frontend on Azure Static Web Apps
- Auth to Azure services: managed identity / DefaultAzureCredential, no keys in code
- Infrastructure as code: Bicep

## User flow
1. User pastes a URL and clicks "Transform"
2. A card appears immediately showing the URL and a progress state
3. The card updates through these states: "Reading article…" → "Generating audio…" → "Ready"
4. Once the text is ready, show a preview of the spoken text
5. When done, show an audio player + download button
6. On failure, show a human-readable reason (e.g. "Page needs JavaScript",
   "Article is behind a paywall") and a Retry button
7. A list of all previous articles with their status, newest first

## Processing pipeline
1. Fetch the page HTML in our own code (the AI must NOT browse the URL itself)
2. Extract the main article text using SmartReader (NuGet)
3. Rewrite the text with Azure OpenAI into plain spoken prose: no tables, lists,
   code or image references – describe them in sentences instead
4. Convert the text to speech with Azure AI Speech
5. Store the MP3 in Blob Storage, keep the blob private, serve it to the browser via short-lived SAS

## Reliability requirements (important)
- The API must respond instantly; all processing happens asynchronously in the worker
- Saving the article and scheduling its processing must never get out of sync:
  no article stuck forever without being processed, no processing without an article
- The worker may crash at any point; after restart, work must continue where it left off
- Processing an article twice must never produce duplicate audio or corrupt data
- Transient failures (timeouts, 429, 5xx) are retried automatically, max 3 attempts;
  permanent failures (404, paywall, empty page) fail immediately
- One broken article must never block processing of other articles
- Store which step failed and why, so the UI can show it and Retry resumes from that step
- Re-generating audio with a different voice must not require fetching the page again

## Cost
- Hobby project, single user, should cost only a few euros per month when idle

## Deliverables
- Working solution: API, worker, frontend, Bicep
- Runs locally (document emulators / local setup)
- README explaining: the Cosmos DB data model (containers, item shapes, keys)
  and WHY you designed it that way, plus how each reliability requirement is met
```

</details>

## Step 3: First attempt without MCP tools or skills

Round 1 ran in a plain Claude Code session in this folder. No Azure MCP server and no Cosmos DB agent kit skill were enabled, so every Cosmos DB decision came only from the model's own knowledge.

## Step 4: What the agent generated

From the single prompt, the agent produced the whole solution:

- `src/AudioBlog.Api`: minimal API
- `src/AudioBlog.Worker`: background worker (change feed plus a sweep for due work)
- `src/AudioBlog.Core`: domain, Cosmos DB data access, pipeline (extract, rewrite, synthesize), Blob storage
- `tests/AudioBlog.Tests`: unit tests
- `web/`: React frontend
- `infra/`: Bicep for Cosmos DB (serverless), Storage, AI, Container Apps, Static Web Apps
- `docker-compose.yml`: Cosmos DB emulator and Azurite
- [`docs/DESIGN.md`](docs/DESIGN.md): the data model, why it was designed that way, and how each reliability requirement is met

The Cosmos DB design it chose on its own:

- **Partition key:** hierarchical `[userId, articleId]`. All items of one article share a logical partition, so step output and status can be written in one transactional batch.
- **Item types:** a small, frequently updated `article` status item, separate from the large `source` and `script` text items.
- **Concurrency:** ETag checks on every read-modify-write, plus leases so two workers can't process the same article.
- **Scheduling:** "save + schedule" is one write (`dueAt` on the article). The worker picks work up through the change feed, with a periodic sweep as a safety net.
- **Indexing:** exclude `/*`, include only the queried paths, plus a composite index for the article list.
- **SDK:** one singleton `CosmosClient`, Direct mode in Azure, Gateway mode against the emulator.

## Step 5: Install, build, test, run

These are the commands used. They are also how you run it yourself.

**1. Start the emulators** (Cosmos DB vNext emulator on https://localhost:8081, Data Explorer on http://localhost:1234, Azurite on port 10000):

```bash
docker compose up -d
```

**2. Deploy the AI models to Azure and grant your `az login` user access** (creates the resource group `rg-audioblog-ai`, no keys):

```bash
pwsh ./scripts/setup-local-ai.ps1
```

**3. Build and run the tests:**

```bash
dotnet build AudioBlog.slnx
```

```bash
dotnet test AudioBlog.slnx
```

**4. Start the API, the worker and the frontend**, each in its own terminal:

```bash
dotnet run --project src/AudioBlog.Api
```

```bash
dotnet run --project src/AudioBlog.Worker
```

```bash
npm --prefix web install && npm --prefix web run dev
```

Then open http://localhost:5173 and transform an article.

The containers and the database are created on startup (`EnsureCreated` in `appsettings.Development.json`), so no manual setup in the emulator is needed. Both AI services are pay-per-use with no idle cost. Remove them afterwards with:

```bash
az group delete --name rg-audioblog-ai
```

## Step 6: What worked and what didn't

**Result of round 1:** the generated code **built on the first try**, **all 33 unit tests passed**, and the app **ran locally end to end** against the Cosmos DB emulator. The Cosmos DB part worked without any fixes.

It took **one prompt plus two follow-ups** to get real audio out of it. Neither follow-up was about Cosmos DB:

| # | Problem | Cause | Fix |
|---|---|---|---|
| 1 | **The MP3 was silent.** The pipeline ran through all states to "Ready", but the audio had no speech. | Locally the worker ran in the built-in offline `Fake` AI mode (`"AiMode": "Fake"` in `appsettings.Development.json`). There is no local emulator for Azure OpenAI or Speech, so the fake produces silence of the right length. | Follow-up 1: the agent added an AI-only Bicep ([`infra/ai-only.bicep`](infra/ai-only.bicep)) and [`scripts/setup-local-ai.ps1`](scripts/setup-local-ai.ps1), which deploy just the two AI services and write a git-ignored `appsettings.Local.json` that switches the worker to them. |
| 2 | **Azure sign-in failed locally** with `AuthenticationFailedException: ManagedIdentityCredential authentication failed: All Managed Identity sources are unavailable`. | `DefaultAzureCredential` tried managed identity first, which doesn't exist on a laptop. | Follow-up 2: managed identity and workload identity are now only used when a client ID is configured ([`ServiceCollectionExtensions.cs:53`](src/AudioBlog.Core/ServiceCollectionExtensions.cs:53)), so locally it falls through to the Azure CLI login. |

## Step 7: Installing the agent kit

The `cosmosdb-best-practices` skill (100+ rules in 12 categories: data modeling, partition keys, queries, SDK, indexing, throughput, …) was installed into `.claude/skills/cosmosdb-best-practices` and pinned in `skills-lock.json`:

```bash
npx skills add AzureCosmosDB/cosmosdb-agent-kit --skill cosmosdb-best-practices --agent claude-code --copy -y
```

Inside Claude Code you can instead run `/plugin install cosmosdb@claude-plugins-official`, the kit's official Claude Code install. No Azure MCP server was added; only the skill.

## Step 8: What the Cosmos DB review suggested

A fresh Claude Code session reviewed the baseline with the skill.

**What the review kept as-is.** The baseline already followed most of the kit's rules:

- **Partition key:** hierarchical `[userId, articleId]`. All items of one article share a logical partition (transactional batch), and "list my articles" is a single-prefix query, not a fan-out.
- **Reads and writes:** point reads for everything addressed by id, a transactional batch for step output + status, ETag checks on every read-modify-write.
- **SDK:** one singleton `CosmosClient` in Direct mode, enums stored as strings, fixed-width UTC timestamps, `EnableContentResponseOnWrite = false` where the response isn't needed.
- **Data model:** the small, frequently updated status item is kept apart from the large text items.
- **Indexing:** exclude `/*`, include only the queried paths.
- **Security:** Entra ID only, `disableLocalAuth: true`.

**Suggestions, highest impact first:**

| # | Suggestion | Where | Why |
|---|---|---|---|
| 1 | **Make the list query use its composite index.** Change `ORDER BY c.createdAt DESC` to `ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC`. | [`CosmosArticleStore.cs:85`](src/AudioBlog.Core/Data/CosmosArticleStore.cs:85) | Cosmos DB only uses a composite index for filter + sort when the equality-filtered fields come first in `ORDER BY`. As written, the query falls back to the single-field `createdAt` index and filters afterwards. The result order doesn't change (both fields are constant after the filter). This is the busiest query: the UI polls it every 2 s while an article is processing. |
| 2 | **Log Cosmos diagnostics and RU charges.** Set `CosmosClientTelemetryOptions` with `CosmosThresholdOptions`, enable distributed tracing / OpenTelemetry, and keep the inner `CosmosException` (or its diagnostics) on `ConcurrencyException` and `CosmosBatchException`. | [`CosmosSetup.cs`](src/AudioBlog.Core/Data/CosmosSetup.cs), [`CosmosArticleStore.cs:51`](src/AudioBlog.Core/Data/CosmosArticleStore.cs:51), [`:73`](src/AudioBlog.Core/Data/CosmosArticleStore.cs:73) | Nothing records diagnostics, request charges or slow requests today, and the custom exceptions throw the diagnostics away. Without them you can't tell throttling, retries, fan-out and network latency apart, or measure the effect of #1. |
| 3 | **Make the background cross-partition queries cheaper.** Replace the `COUNT` in `CountPendingAsync` with `SELECT TOP 1 VALUE 1 … WHERE c.type = @type AND IS_DEFINED(c.dueAt)`, or drop the query and use the dispatcher's in-memory state plus the last sweep result. In `QueryDueAsync`, set `MaxItemCount = maxItems` so a page can't go past the limit. | [`CosmosArticleStore.cs:99-128`](src/AudioBlog.Core/Data/CosmosArticleStore.cs:99) | The keep-alive only needs yes/no but counts every pending article every 60 s. Both loops fan out to every physical partition around the clock, and on serverless every request is billed. Small today, grows as data spreads over more partitions. |
| 4 | **Add a schema version to every document.** `"schemaVersion": 1` on article, source and script items. | [`Documents.cs`](src/AudioBlog.Core/Domain/Documents.cs) | `ArticleDocument` has already changed shape several times (`Steps`, `Progress`, `Lease`, `Failure`). Stored documents keep their old shape, and old workers can still run during a deployment. A version field allows gradual or on-read migration instead of guessing from missing fields. |
| 5 | **Keep the indexing policy in sync between Bicep and code.** After startup, read the container, compare the composite indexes and call `ReplaceContainerAsync` if they differ, or keep one shared source for the policy. | [`CosmosSetup.cs`](src/AudioBlog.Core/Data/CosmosSetup.cs), [`cosmos.bicep`](infra/modules/cosmos.bicep) | `CreateContainerIfNotExistsAsync` ignores the indexing policy for an existing container, so an emulator container from an older schema keeps its old indexes. The two definitions have already drifted: Bicep excludes `/"_etag"/?`, the C# version doesn't. |

**Lower priority:**

- **Patch instead of Replace for progress updates.** [`ArticleProcessor.cs:191-200`](src/AudioBlog.Core/Pipeline/ArticleProcessor.cs:191) rewrites the whole article for every speech chunk. `PatchItemAsync` on `/progress` with `IfMatchEtag` sends less and states what changed. The RU saving is small because the document is small.
- **Project fields in the list query.** `SELECT *` also returns `lease`, `dueAt`, `failure.detail` and step bookkeeping the DTO never uses. Small gain, mostly tidier.
- **429 retry settings.** The values in [`CosmosSetup.cs`](src/AudioBlog.Core/Data/CosmosSetup.cs) equal the SDK defaults, so they change nothing. Harmless.
- **Availability.** One region, no zone redundancy, no `ApplicationRegion`. Fine for a hobby app on serverless; if it grows, zone redundancy is the cheapest first step.

## Step 9: Measuring the RU effect of the review

The review's three query changes were measured against **real Azure Cosmos DB**, before (round 1) and after (round 2). The local vNext emulator can't be used for this. Its [feature table](https://learn.microsoft.com/azure/cosmos-db/emulator-linux#feature-support) lists Request Units as "not yet implemented", range and composite indexes as not yet available (a custom indexing policy is accepted but ignored), and throughput changes as not implemented, so containers with several physical partitions can't be built.

### Test setup

| | |
|---|---|
| **Cosmos DB account** | Provisioned throughput (manual), single region West Europe, Entra ID only: [`infra/cosmos-partitions.bicep`](infra/cosmos-partitions.bicep) |
| **Containers** | `p1` = 6,000 RU/s → **1** physical partition, `p5` = 30,000 RU/s → **5**, `p10` = 60,000 RU/s → **10**. A new container starts with RU/s ÷ 6,000 physical partitions. |
| **Container definition** | Same partition key (`[/userId, /articleId]`) and indexing policy as the app's `articles` container: [`infra/modules/articles-container.bicep`](infra/modules/articles-container.bicep) |
| **Test data** | Article items shaped like the app's (~1.5 KB), 1,000 / 10,000 / 100,000 per container |
| **Data shapes** | **single**: all articles belong to one user (this app today; one partition key prefix → one physical partition). **many**: articles spread over 1,000 users (data in every physical partition). |
| **Scenarios** | For the due sweep and pending check: none, one, 10 % and all articles made due (`dueAt` in the past) |
| **Queries** | Before = the round-1 versions, after = the round-2 versions from [`CosmosArticleStore.cs`](src/AudioBlog.Core/Data/CosmosArticleStore.cs); both are verbatim in [`scripts/ru-test.cs`](scripts/ru-test.cs) |
| **Measurement** | Request charge (RU) reported by Cosmos DB, summed over all pages of a query; median of 5 runs after one warm-up run. *Partitions hit* comes from the per-partition server-side query metrics. *Same result* checks that before and after returned the same answer. |
| **Script** | [`scripts/ru-test.cs`](scripts/ru-test.cs) (.NET 10 file-based app): `dotnet run scripts/ru-test.cs -- --endpoint <cosmos endpoint>` |
| **Run** | 2026-10-21, about 35 minutes. `p1` + `p5` and `p10` ran as two parallel processes; both reports were merged. The `p1` + `p5` run was repeated after a timeout. Its **single** rows were measured right after deleting ~100,000 items, which inflated the list query's cost (23.63 → 10.56 RU at 1,000 articles), so the single rows come from the first run on empty containers. |
| **Cost** | About $15 (two started hours at 96,000 RU/s); the account was deleted afterwards |
| **Full results file** | [`docs/ru-results/ru-test-partitions-20261021.md`](docs/ru-results/ru-test-partitions-20261021.md) |


### Physical partitions holding data

| Container | Physical partitions | Shape | Articles | Partitions with data |
|---|---:|---|---:|---:|
| p1 | 1 | single | 1,000 | 1 |
| p1 | 1 | single | 10,000 | 1 |
| p1 | 1 | single | 100,000 | 1 |
| p1 | 1 | many | 1,000 | 1 |
| p1 | 1 | many | 10,000 | 1 |
| p1 | 1 | many | 100,000 | 1 |
| p5 | 5 | single | 1,000 | 1 |
| p5 | 5 | single | 10,000 | 1 |
| p5 | 5 | single | 100,000 | 1 |
| p5 | 5 | many | 1,000 | 5 |
| p5 | 5 | many | 10,000 | 5 |
| p5 | 5 | many | 100,000 | 5 |
| p10 | 10 | single | 1,000 | 1 |
| p10 | 10 | single | 10,000 | 1 |
| p10 | 10 | single | 100,000 | 1 |
| p10 | 10 | many | 1,000 | 10 |
| p10 | 10 | many | 10,000 | 10 |
| p10 | 10 | many | 100,000 | 10 |

### 1. List articles, newest first (`ListArticlesAsync`)

- **Before:** `... ORDER BY c.createdAt DESC`
- **After:** `... ORDER BY c.userId ASC, c.type ASC, c.createdAt DESC`
- Scoped to one user (partition key prefix). Runs on page load and every 2 s while an article is in progress. *Same result* = identical items in identical order.

| Partitions | Shape | Articles | Scenario | Before RU | After RU | Saved RU | Saved % | Partitions hit | Same result |
|---:|---|---:|---|---:|---:|---:|---:|:---:|:---:|
| 1 | single | 1,000 | first page (≤100) | 7.60 | 7.77 | -0.17 | -2.2 % | 1 → 1 | ✓ |
| 1 | single | 10,000 | first page (≤100) | 7.62 | 7.80 | -0.18 | -2.4 % | 1 → 1 | ✓ |
| 1 | single | 100,000 | first page (≤100) | 7.62 | 7.78 | -0.16 | -2.1 % | 1 → 1 | ✓ |
| 1 | many | 1,000 | first page (≤100) | 2.84 | 2.85 | -0.01 | -0.4 % | 1 → 1 | ✓ |
| 1 | many | 10,000 | first page (≤100) | 3.25 | 3.29 | -0.04 | -1.2 % | 1 → 1 | ✓ |
| 1 | many | 100,000 | first page (≤100) | 7.37 | 7.78 | -0.41 | -5.6 % | 1 → 1 | ✓ |
| 5 | single | 1,000 | first page (≤100) | 7.60 | 7.77 | -0.17 | -2.2 % | 1 → 1 | ✓ |
| 5 | single | 10,000 | first page (≤100) | 7.59 | 7.95 | -0.36 | -4.7 % | 1 → 1 | ✓ |
| 5 | single | 100,000 | first page (≤100) | 7.61 | 7.79 | -0.18 | -2.4 % | 1 → 1 | ✓ |
| 5 | many | 1,000 | first page (≤100) | 2.94 | 2.85 | 0.09 | 3.1 % | 1 → 1 | ✓ |
| 5 | many | 10,000 | first page (≤100) | 3.35 | 3.30 | 0.05 | 1.5 % | 1 → 1 | ✓ |
| 5 | many | 100,000 | first page (≤100) | 7.46 | 7.87 | -0.41 | -5.5 % | 1 → 1 | ✓ |
| 10 | single | 1,000 | first page (≤100) | 7.60 | 7.77 | -0.17 | -2.2 % | 1 → 1 | ✓ |
| 10 | single | 10,000 | first page (≤100) | 7.60 | 7.90 | -0.30 | -3.9 % | 1 → 1 | ✓ |
| 10 | single | 100,000 | first page (≤100) | 7.70 | 7.79 | -0.09 | -1.2 % | 1 → 1 | ✓ |
| 10 | many | 1,000 | first page (≤100) | 2.84 | 2.85 | -0.01 | -0.4 % | 1 → 1 | ✓ |
| 10 | many | 10,000 | first page (≤100) | 3.25 | 3.29 | -0.04 | -1.2 % | 1 → 1 | ✓ |
| 10 | many | 100,000 | first page (≤100) | 7.46 | 7.87 | -0.41 | -5.5 % | 1 → 1 | ✓ |

### 2. Due-work sweep (`QueryDueAsync`)

- **Before:** `SELECT TOP @max c.userId, c.articleId FROM c WHERE c.type = @type AND c.dueAt <= @now`, no request options
- **After:** Same SQL, with `MaxItemCount = maxItems`
- Cross-partition. Runs every 15 s in the worker (172,800 calls / month) with `maxItems = 100`. *Same result* = same number of due articles.

| Partitions | Shape | Articles | Scenario | Before RU | After RU | Saved RU | Saved % | Partitions hit | Before RU / month | After RU / month | Same result |
|---:|---|---:|---|---:|---:|---:|---:|:---:|---:|---:|:---:|
| 1 | single | 1,000 | 0 due | 2.79 | 2.79 | 0.00 | 0.0 % | 1 → 1 | 482,112 | 482,112 | ✓ |
| 1 | single | 1,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | single | 1,000 | 100 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | single | 1,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | single | 10,000 | 0 due | 2.79 | 2.79 | 0.00 | 0.0 % | 1 → 1 | 482,112 | 482,112 | ✓ |
| 1 | single | 10,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | single | 10,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | single | 10,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | single | 100,000 | 0 due | 2.79 | 2.79 | 0.00 | 0.0 % | 1 → 1 | 482,112 | 482,112 | ✓ |
| 1 | single | 100,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | single | 100,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | single | 100,000 | 100,000 due | 5.56 | 5.66 | -0.10 | -1.8 % | 1 → 1 | 960,768 | 978,048 | ✓ |
| 1 | many | 1,000 | 0 due | 2.89 | 2.79 | 0.10 | 3.5 % | 1 → 1 | 499,392 | 482,112 | ✓ |
| 1 | many | 1,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | many | 1,000 | 100 due | 5.56 | 5.46 | 0.10 | 1.8 % | 1 → 1 | 960,768 | 943,488 | ✓ |
| 1 | many | 1,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | many | 10,000 | 0 due | 2.79 | 2.89 | -0.10 | -3.6 % | 1 → 1 | 482,112 | 499,392 | ✓ |
| 1 | many | 10,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | many | 10,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | many | 10,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | many | 100,000 | 0 due | 2.79 | 2.79 | 0.00 | 0.0 % | 1 → 1 | 482,112 | 482,112 | ✓ |
| 1 | many | 100,000 | 1 due | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 487,296 | 487,296 | ✓ |
| 1 | many | 100,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 1 | many | 100,000 | 100,000 due | 5.47 | 5.47 | 0.00 | 0.0 % | 1 → 1 | 945,216 | 945,216 | ✓ |
| 5 | single | 1,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | single | 1,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | single | 1,000 | 100 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | single | 1,000 | 1,000 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | single | 10,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | single | 10,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | single | 10,000 | 1,000 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | single | 10,000 | 10,000 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | single | 100,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | single | 100,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | single | 100,000 | 10,000 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | single | 100,000 | 100,000 due | 13.83 | 13.83 | 0.00 | 0.0 % | 4 → 4 | 2,389,824 | 2,389,824 | ✓ |
| 5 | many | 1,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | many | 1,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | many | 1,000 | 100 due | 16.66 | 16.66 | 0.00 | 0.0 % | 5 → 5 | 2,878,848 | 2,878,848 | ✓ |
| 5 | many | 1,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 5 | many | 10,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | many | 10,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | many | 10,000 | 1,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 5 | many | 10,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 5 | many | 100,000 | 0 due | 13.95 | 13.95 | 0.00 | 0.0 % | 5 → 5 | 2,410,560 | 2,410,560 | ✓ |
| 5 | many | 100,000 | 1 due | 13.98 | 13.98 | 0.00 | 0.0 % | 5 → 5 | 2,415,744 | 2,415,744 | ✓ |
| 5 | many | 100,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 5 | many | 100,000 | 100,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 10 | single | 1,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | single | 1,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | single | 1,000 | 100 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | single | 1,000 | 1,000 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | single | 10,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | single | 10,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | single | 10,000 | 1,000 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | single | 10,000 | 10,000 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | single | 100,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | single | 100,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | single | 100,000 | 10,000 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | single | 100,000 | 100,000 due | 22.20 | 22.20 | 0.00 | 0.0 % | 7 → 7 | 3,836,160 | 3,836,160 | ✓ |
| 10 | many | 1,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | many | 1,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | many | 1,000 | 100 due | 30.65 | 30.65 | 0.00 | 0.0 % | 10 → 10 | 5,296,320 | 5,296,320 | ✓ |
| 10 | many | 1,000 | 1,000 due | 10.68 | 10.68 | 0.00 | 0.0 % | 2 → 2 | 1,845,504 | 1,845,504 | ✓ |
| 10 | many | 10,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | many | 10,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | many | 10,000 | 1,000 due | 10.68 | 10.68 | 0.00 | 0.0 % | 2 → 2 | 1,845,504 | 1,845,504 | ✓ |
| 10 | many | 10,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 10 | many | 100,000 | 0 due | 27.90 | 27.90 | 0.00 | 0.0 % | 10 → 10 | 4,821,120 | 4,821,120 | ✓ |
| 10 | many | 100,000 | 1 due | 27.93 | 27.93 | 0.00 | 0.0 % | 10 → 10 | 4,826,304 | 4,826,304 | ✓ |
| 10 | many | 100,000 | 10,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |
| 10 | many | 100,000 | 100,000 due | 5.46 | 5.46 | 0.00 | 0.0 % | 1 → 1 | 943,488 | 943,488 | ✓ |

### 3. Pending check for the scale-to-zero keep-alive (`CountPendingAsync` → `HasPendingAsync`)

- **Before:** `SELECT VALUE COUNT(1) FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)`
- **After:** `SELECT TOP 1 VALUE 1 FROM c WHERE c.type = @type AND IS_DEFINED(c.dueAt)` with `MaxItemCount = 1`
- Cross-partition. Runs once a minute while the worker is awake (43,200 calls / month). *Same result* = both agree whether anything is pending.

| Partitions | Shape | Articles | Scenario | Before RU | After RU | Saved RU | Saved % | Partitions hit | Before RU / month | After RU / month | Same result |
|---:|---|---:|---|---:|---:|---:|---:|:---:|---:|---:|:---:|
| 1 | single | 1,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | single | 1,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | single | 1,000 | 100 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | single | 1,000 | 1,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | single | 10,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | single | 10,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | single | 10,000 | 1,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | single | 10,000 | 10,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | single | 100,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | single | 100,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | single | 100,000 | 10,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | single | 100,000 | 100,000 pending | 3.10 | 2.92 | 0.18 | 5.8 % | 1 → 1 | 133,920 | 126,144 | ✓ |
| 1 | many | 1,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | many | 1,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | many | 1,000 | 100 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | many | 1,000 | 1,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | many | 10,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | many | 10,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | many | 10,000 | 1,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | many | 10,000 | 10,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | many | 100,000 | 0 pending | 2.80 | 2.79 | 0.01 | 0.4 % | 1 → 1 | 120,960 | 120,528 | ✓ |
| 1 | many | 100,000 | 1 pending | 2.82 | 2.82 | 0.00 | 0.0 % | 1 → 1 | 121,824 | 121,824 | ✓ |
| 1 | many | 100,000 | 10,000 pending | 2.99 | 2.82 | 0.17 | 5.7 % | 1 → 1 | 129,168 | 121,824 | ✓ |
| 1 | many | 100,000 | 100,000 pending | 3.00 | 2.82 | 0.18 | 6.0 % | 1 → 1 | 129,600 | 121,824 | ✓ |
| 5 | single | 1,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | single | 1,000 | 1 pending | 14.02 | 11.19 | 2.83 | 20.2 % | 5 → 4 | 605,664 | 483,408 | ✓ |
| 5 | single | 1,000 | 100 pending | 14.19 | 11.19 | 3.00 | 21.1 % | 5 → 4 | 613,008 | 483,408 | ✓ |
| 5 | single | 1,000 | 1,000 pending | 14.19 | 11.19 | 3.00 | 21.1 % | 5 → 4 | 613,008 | 483,408 | ✓ |
| 5 | single | 10,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | single | 10,000 | 1 pending | 14.02 | 11.19 | 2.83 | 20.2 % | 5 → 4 | 605,664 | 483,408 | ✓ |
| 5 | single | 10,000 | 1,000 pending | 14.19 | 11.19 | 3.00 | 21.1 % | 5 → 4 | 613,008 | 483,408 | ✓ |
| 5 | single | 10,000 | 10,000 pending | 14.19 | 11.19 | 3.00 | 21.1 % | 5 → 4 | 613,008 | 483,408 | ✓ |
| 5 | single | 100,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | single | 100,000 | 1 pending | 14.02 | 11.19 | 2.83 | 20.2 % | 5 → 4 | 605,664 | 483,408 | ✓ |
| 5 | single | 100,000 | 10,000 pending | 14.19 | 11.19 | 3.00 | 21.1 % | 5 → 4 | 613,008 | 483,408 | ✓ |
| 5 | single | 100,000 | 100,000 pending | 14.20 | 11.19 | 3.01 | 21.2 % | 5 → 4 | 613,440 | 483,408 | ✓ |
| 5 | many | 1,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | many | 1,000 | 1 pending | 14.02 | 5.61 | 8.41 | 60.0 % | 5 → 2 | 605,664 | 242,352 | ✓ |
| 5 | many | 1,000 | 100 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 5 | many | 1,000 | 1,000 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 5 | many | 10,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | many | 10,000 | 1 pending | 14.02 | 5.61 | 8.41 | 60.0 % | 5 → 2 | 605,664 | 242,352 | ✓ |
| 5 | many | 10,000 | 1,000 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 5 | many | 10,000 | 10,000 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 5 | many | 100,000 | 0 pending | 14.00 | 13.95 | 0.05 | 0.4 % | 5 → 5 | 604,800 | 602,640 | ✓ |
| 5 | many | 100,000 | 1 pending | 14.02 | 5.61 | 8.41 | 60.0 % | 5 → 2 | 605,664 | 242,352 | ✓ |
| 5 | many | 100,000 | 10,000 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 5 | many | 100,000 | 100,000 pending | 14.95 | 2.82 | 12.13 | 81.1 % | 5 → 1 | 645,840 | 121,824 | ✓ |
| 10 | single | 1,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | single | 1,000 | 1 pending | 28.02 | 19.56 | 8.46 | 30.2 % | 10 → 7 | 1,210,464 | 844,992 | ✓ |
| 10 | single | 1,000 | 100 pending | 28.19 | 19.56 | 8.63 | 30.6 % | 10 → 7 | 1,217,808 | 844,992 | ✓ |
| 10 | single | 1,000 | 1,000 pending | 28.19 | 19.56 | 8.63 | 30.6 % | 10 → 7 | 1,217,808 | 844,992 | ✓ |
| 10 | single | 10,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | single | 10,000 | 1 pending | 28.02 | 19.56 | 8.46 | 30.2 % | 10 → 7 | 1,210,464 | 844,992 | ✓ |
| 10 | single | 10,000 | 1,000 pending | 28.19 | 19.56 | 8.63 | 30.6 % | 10 → 7 | 1,217,808 | 844,992 | ✓ |
| 10 | single | 10,000 | 10,000 pending | 28.19 | 19.56 | 8.63 | 30.6 % | 10 → 7 | 1,217,808 | 844,992 | ✓ |
| 10 | single | 100,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | single | 100,000 | 1 pending | 28.02 | 19.56 | 8.46 | 30.2 % | 10 → 7 | 1,210,464 | 844,992 | ✓ |
| 10 | single | 100,000 | 10,000 pending | 28.19 | 19.56 | 8.63 | 30.6 % | 10 → 7 | 1,217,808 | 844,992 | ✓ |
| 10 | single | 100,000 | 100,000 pending | 28.20 | 19.56 | 8.64 | 30.6 % | 10 → 7 | 1,218,240 | 844,992 | ✓ |
| 10 | many | 1,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | many | 1,000 | 1 pending | 28.02 | 8.40 | 19.62 | 70.0 % | 10 → 3 | 1,210,464 | 362,880 | ✓ |
| 10 | many | 1,000 | 100 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
| 10 | many | 1,000 | 1,000 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
| 10 | many | 10,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | many | 10,000 | 1 pending | 28.02 | 8.40 | 19.62 | 70.0 % | 10 → 3 | 1,210,464 | 362,880 | ✓ |
| 10 | many | 10,000 | 1,000 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
| 10 | many | 10,000 | 10,000 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
| 10 | many | 100,000 | 0 pending | 28.00 | 27.90 | 0.10 | 0.4 % | 10 → 10 | 1,209,600 | 1,205,280 | ✓ |
| 10 | many | 100,000 | 1 pending | 28.02 | 8.40 | 19.62 | 70.0 % | 10 → 3 | 1,210,464 | 362,880 | ✓ |
| 10 | many | 100,000 | 10,000 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
| 10 | many | 100,000 | 100,000 pending | 29.90 | 2.82 | 27.08 | 90.6 % | 10 → 1 | 1,291,680 | 121,824 | ✓ |
