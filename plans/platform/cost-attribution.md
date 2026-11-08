# Cost attribution: plan

Who pays for each model call Ume.Rag makes, and how departments see and limit it. Status: **plan** (main session,
2026-11-08), decided in Decisions "Cost attribution (user, 2026-11-08)". The long-term goal is **delegated attribution
in the gateway** (§4, stage 2); Ume.Rag's own metering (§5, stage 1) comes first and stays as the finer breakdown. The
design documents named in each step bind where they and this plan differ; each step updates them before code.

## 1. Goal

- **Departments pay for their own use.** Every model call is charged in the gateway to the cost centre (department)
  whose assistant or knowledge base caused it, so the gateway's reports, Data API and budgets are the record for
  internal charging.
- **Budgets can stop an assistant.** A department can give an assistant a budget of its own that the gateway enforces.
- **Editors see what their assistants and knowledge bases cost**, finer than the gateway can show: per assistant,
  knowledge base, step (answer, condense, follow-ups, rerank, embeddings, evaluation) and client.

## 2. Where it stands (2026-11-08)

| Part | Today | Reference |
|---|---|---|
| Gateway attribution | **by key only**: a usage record holds the key, its team and department, model, tokens, cost and outcome, and nothing per request (no tag, no end user) | `~/git/llmgateway` `src/Ume.LlmGateway.Domain/Entities/UsageRecord.cs` (v0.9.2) |
| Gateway budgets | per department, team or key, in USD or tokens; a spent budget answers 402 and names the scope | gateway `docs/architecture.md`, `docs/admin-api.md` |
| Gateway reports | admin UI per department (department admins and viewers see their own); Data API `usage.aggregate` per department and cost-centre code, `usage.detail` one row per request with key, team and department | gateway `docs/data-access.md` |
| Ume.Rag's gateway keys | one per level and deployable, secret files in the deployment, owned by the pilot team; **all chat traffic is one line in the gateway** (the api key) | Decisions "Gateway keys" (§7); gateway-changes §13.4 |
| Delegation (`x-ume-on-behalf-of`) | designed (feature A, gateway release 2, ADR 020), **not built** in the gateway | [`../gateway-changes/design.md`](../gateway-changes/design.md) §5, §7.1, §13.4 |
| Ume.Rag's side of delegation | designed: chat sends the assistant's team id, a missing echo fails closed; the header constant exists | [`../identity/design.md`](../identity/design.md) §4.7; `src/Ume.Rag.Infrastructure/Gateway/GatewayOptions.cs` (`OnBehalfOf`) |
| Owner teams in the schema | `Assistants.OwnerTeamId`, `Collections.OwnerTeamId`, `Jobs.TeamId` (nullable), `Teams` mirror with the gateway's ids | [`data-model.md`](data-model.md) §2.1; `src/Ume.Rag.Domain/Entities/AssistantEntities.cs`, `CollectionEntities.cs` |
| Chat metering | `AssistantDailyTotals` per day, assistant and client counts the **answer call's** input and output tokens; its `CostUsd` is written as 0; condense, follow-ups, rerank and query embeddings reach only the metric `rag.chat.tokens{direction, step}` | `src/Ume.Rag.Api/ConversationWriter.cs` (totals upsert); `src/Ume.Rag.Answering/Chat/ChatService.cs` (`CountTokens`) |
| Ingestion metering | cost per run item and run (`RunItems.CostUsd`, `Runs.CostUsd`) | `src/Ume.Rag.Worker/Engine/Runs.cs` |
| Prices | the model mirror holds the gateway's prices (`InputPerMillionUsd`, `CachedInputPerMillionUsd`, `OutputPerMillionUsd`); the gateway answers `x-ume-cost-usd` on non-streamed calls only (streamed answers carry tokens in the usage chunk) | `src/Ume.Rag.Domain/Entities/AssistantEntities.cs`; gateway `GatewayRequestHandler.cs` |

## 3. Options weighed

| Option | What | Verdict |
|---|---|---|
| **A. A gateway key per assistant** | Ume.Rag stores a gateway key per assistant (and level and deployable) and picks it per call | **No.** Against Decisions "Gateway keys" (secret files, not database rows) and the separate key stores (§7): a secret per assistant, level and deployable, a manual gateway step for every new assistant, rotation per key; department admins can revoke a key in the gateway and break an assistant unseen; ingestion would still run on the platform key |
| **B. Delegated attribution** | delegating keys per level and deployable, owned by the platform team; every call says `x-ume-on-behalf-of: <team id>`; the gateway charges, reports and enforces per team and department | **The long-term goal** (stage 2, §4): the gateway stays the record and the enforcer, no secret per assistant, works for ingestion too |
| **C. Ume.Rag meters itself** | tokens and cost of every model call recorded per assistant, knowledge base, step and client, from the mirror's prices | **First** (stage 1, §5): no gateway dependency, finer than the gateway, reporting only (no hard stop), an estimate beside the gateway's record |
| D. A free tag on gateway usage records | e.g. the assistant id in a header stored on the usage record | **No.** Reporting only, and B with C covers it; the run headers of gateway-changes §7.1 stay opaque run ids |

## 4. Target model (stage 2, delegated attribution)

### 4.1 Who pays for which call

The attribution unit is a **gateway team**; its department (cost centre) follows from it.

| Call | Deployable | Charged to |
|---|---|---|
| Chat answer, condense, long-question condense, follow-up suggestions, query embedding, rerank | api | the assistant's owner team |
| Assistant test panel | admin | the assistant's owner team |
| Retrieval test (admin §4.9), preview, preflight | admin, worker | the knowledge base's owner team |
| Ingestion embeddings, reprocessing | worker | the knowledge base's owner team (`Jobs.TeamId`) |
| Evaluation: question generation, judge, three-way runs | worker | the knowledge base's owner team |
| Model check on assistant save (`GET /v1/models`, V8) | admin | the assistant's owner team (no cost; the call checks the delegation early) |
| Model sync, embedding canary, organisation sync | admin | the platform team (not delegated) |

An assistant that searches another department's shared knowledge base pays for its own questions; the knowledge
base's owner pays for keeping it indexed.

### 4.2 Rules

- **Owner team required.** Every assistant and knowledge base has an owner team: a team of its own department, active,
  chosen in the create and edit drawers (default: the department's default team, the first active team by name until
  the department sets one). Under "No backward compatibility before production" existing rows get the default in the
  migration.
- **Per-assistant budgets by convention:** a department that wants a hard limit for one assistant creates a gateway
  team for it (e.g. *DoIT › Supportassistenten*), sets the budget there in the gateway, and makes it the assistant's
  owner team. No Ume.Rag setting holds a budget.
- **Keys:** delegating keys per level and deployable replace the pilot team's keys (gateway-changes §13.4), owned by the
  platform team, `RequireMaxResidency` on, `ApplyTeamPolicy` off. The worker never holds the open key.
- **Every delegated call** sends `x-ume-on-behalf-of: <team id>` (never a code); the echo is checked and a missing echo
  fails closed (`Gateway:RequireDelegationEcho`, identity §4.7; security event 5011).
- **A spent budget** (gateway 402 `budget_exceeded`, naming the scope) is shown to the chat user as the assistant being
  unavailable for now, counted in the daily totals, and raised as an attention item for the assistant's department
  admins; a worker job on a spent budget waits and retries after the period rolls over, it does not fail the document.
- **Delegation list:** a delegating key only acts for the teams it lists (`DelegatedTeamIds`, gateway-changes §5.1).
  Choosing an owner team that the key does not list must be refused on save, not at the first chat (Q2).

## 5. Stage 1: Ume.Rag's own metering (first)

Reporting only, no gateway change. Prices are the mirror's at the time of the call; the gateway's own figures stay the
record for charging (they also count fallbacks and retries the caller does not see).

| # | Step | Owner | Design to update first |
|---|---|---|---|
| C1 | Chat records tokens **and cost** of every model call per day, assistant and client: the answer (as today) plus condense, long-question condense, follow-ups, rerank and the query embedding; cost from the mirror's prices (cached input at its own price), `x-ume-cost-usd` where the gateway sends it | b | chat §17, data-model §2 (`AssistantDailyTotals`; a per-step split if the totals row is not enough) |
| C2 | The test panel's calls counted the same way, under the admin's own client row | b | admin §4.9 |
| C3 | Worker: ingestion and evaluation cost per knowledge base per day (from `RunItems.CostUsd` and the evaluation runs), checked to be filled for every model call | a | processing §11, §14; engine §10 |
| C4 | Admin API: cost report per assistant, knowledge base and department, by day or month, with CSV; department admins see their own departments, platform admins all | b | admin §8 (a new subsection) |
| C5 | Admin UI: cost on the assistant page and the knowledge base page, and a cost view per department | b | admin §5.5, §8 |
| C6 | Runbook: how to read the figures beside the gateway's | a | `docs/runbook.md` |

**Done when:** a day of chat on two assistants and one ingestion run show per assistant and knowledge base in the admin
UI and the CSV, and the totals agree with the gateway's usage for the same keys and day within the mirror's price
precision (fallbacks and retries explained).

## 6. Stage 2: delegated attribution (the long-term goal)

| # | Step | Owner | Reference |
|---|---|---|---|
| B1 | Gateway release 2, feature A: `DelegatedTeamIds`, `ApplyTeamPolicy`, `x-ume-on-behalf-of` with its echo, delegated views, `Delegated` on usage records, event 1006, reports and Data API; the run headers of §7.1 in the same migration | gateway (user) | gateway-changes §5, §7.1, §13; ADR 020 |
| B2 | Owner team required on assistants and knowledge bases: migration with the default, team picker in the drawers, a V-rule (active team of the same department, listed by the delegating key) | b + main (V-rule) | admin §4.6, data-model §2.1 |
| B3 | Every gateway call sends `x-ume-on-behalf-of` per §4.1 and checks the echo (`Gateway:RequireDelegationEcho`, 5011); the FakeGateway already echoes | b (api, admin) + a (worker) | identity §4.7 |
| B4 | Delegating keys per level and deployable in place of the pilot team's keys; runbook for adding a team to the delegation list | operator + gateway admin | gateway-changes §13.4 |
| B5 | Organisation sync reads team codes; teams shown in the admin UI's organisation page | b | gateway-changes §13.4 |
| B6 | 402 handling of §4.2 in chat and the worker; the department attention item | b + a | chat §3.6, engine §9 |
| B7 | Stage 1's reports gain the team and, optionally, read the gateway's `usage.aggregate` per department to show the record next to the estimate | b | gateway `docs/data-access.md` |

**Trigger:** before a second department goes live, or earlier if the pilot department wants a hard budget per
assistant. **Done when:** two departments' assistants and knowledge bases are charged to their own teams in the
gateway's usage report, a team budget stops its assistant with the planned message, and the pilot team's keys are
revoked.

## 7. Open questions

| # | Question | Default until answered |
|---|---|---|
| Q1 | Billing granularity: department (cost centre) only, or a gateway team per assistant where a department wants it? | department; per-assistant teams allowed by convention (§4.2) |
| Q2 | Who keeps the delegating keys' team lists current: a gateway admin by hand when a department starts using Ume.Rag, or a gateway option "all teams of these departments"? | by hand, all teams of each department that uses Ume.Rag; a gateway option is proposed to the gateway owner with B1 |
| Q3 | Are hard budgets needed during the pilot? | no: one department, stage 1 reports are enough |
| Q4 | Currency in Ume.Rag's reports | USD, as the gateway (its SEK headers are gone) |
