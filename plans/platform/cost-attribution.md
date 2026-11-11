# Cost attribution: plan

Who pays for each model call Ume.Rag makes, and how departments see and limit it. Status: **plan**, decided in
Decisions "Cost attribution" (user, 2026-11-11, #580). The target is **each assistant and knowledge base on an assigned
gateway key of its department** (§4, stage 2), with the gateway as the record of cost; Ume.Rag's own metering (§5,
stage 1) comes first and stays as the finer breakdown per step. The design documents named in each step bind where
they and this plan differ; each step updates them before code.

## 1. Goal

- **Departments pay for their own use.** Every model call is charged in the gateway to the cost centre (department)
  whose assistant or knowledge base caused it, so the gateway's reports, Data API and budgets are the record for
  internal charging.
- **Budgets can stop an assistant.** A department can give an assistant a budget of its own (its key's) that the
  gateway enforces.
- **Editors see what their assistants and knowledge bases cost**, finer than the gateway can show: per assistant,
  knowledge base, step (answer, condense, follow-ups, rerank, embeddings, evaluation) and client.

## 2. Where it stands (2026-11-08)

| Part | Today | Reference |
|---|---|---|
| Gateway attribution | **by key only**: a usage record holds the key, its team and department, model, tokens, cost and outcome, and nothing per request (no tag, no end user) | `~/git/llmgateway` `src/Ume.LlmGateway.Domain/Entities/UsageRecord.cs` (v0.9.2) |
| Gateway budgets | per department, team or key, in USD or tokens; a spent budget answers 402 and names the scope | gateway `docs/architecture.md`, `docs/admin-api.md` |
| Gateway reports | admin UI per department (department admins and viewers see their own); Data API `usage.aggregate` per department and cost-centre code, `usage.detail` one row per request with key, team and department | gateway `docs/data-access.md` |
| Ume.Rag's gateway keys | one per level and deployable, secret files in the deployment, owned by the pilot team; **all chat traffic is one line in the gateway** (the api key) | Decisions "Gateway keys" (§7); gateway-changes §13.4 |
| Acting as an assigned key (`x-ume-on-behalf-of`) | decided 2026-11-11 (#580): the gateway's integrations design; phase 1 (credential and assigned keys, `/v1/ume/context`, model facts) built in draft PR thekim1/llmgateway#81, phases 2–3 not built | [`../gateway-changes/design.md`](../gateway-changes/design.md) §5, §7.1, §13.4 |
| Ume.Rag's side | designed: every call of an object with a key sends its key id, a missing echo fails closed; the header constant exists | [`../identity/design.md`](../identity/design.md) §4.7; `src/Ume.Rag.Infrastructure/Gateway/GatewayOptions.cs` (`OnBehalfOf`) |
| Owner teams in the schema | `Assistants.OwnerTeamId`, `Collections.OwnerTeamId`, `Jobs.TeamId` (nullable), `Teams` mirror with the gateway's ids | [`data-model.md`](data-model.md) §2.1; `src/Ume.Rag.Domain/Entities/AssistantEntities.cs`, `CollectionEntities.cs` |
| Chat metering | `AssistantDailyTotals` per day, assistant and client counts the **answer call's** input and output tokens; its `CostUsd` is written as 0; condense, follow-ups, rerank and query embeddings reach only the metric `rag.chat.tokens{direction, step}` | `src/Ume.Rag.Api/ConversationWriter.cs` (totals upsert); `src/Ume.Rag.Answering/Chat/ChatService.cs` (`CountTokens`) |
| Ingestion metering | cost per run item and run (`RunItems.CostUsd`, `Runs.CostUsd`) | `src/Ume.Rag.Worker/Engine/Runs.cs` |
| Prices | the model mirror holds the gateway's prices (`InputPerMillionUsd`, `CachedInputPerMillionUsd`, `OutputPerMillionUsd`); the gateway answers `x-ume-cost-usd` on non-streamed calls only (streamed answers carry tokens in the usage chunk) | `src/Ume.Rag.Domain/Entities/AssistantEntities.cs`; gateway `GatewayRequestHandler.cs` |

## 3. Options weighed

| Option | What | Verdict |
|---|---|---|
| **A. A gateway key per assistant** | Ume.Rag stores a gateway key per assistant (and level and deployable) and picks it per call | **No.** Against Decisions "Gateway keys" (secret files, not database rows) and the separate key stores (§7): a secret per assistant, level and deployable, a manual gateway step for every new assistant, rotation per key; department admins can revoke a key in the gateway and break an assistant unseen; ingestion would still run on the platform key |
| **B. Delegated attribution** | delegating keys per level and deployable, owned by the platform team; every call says `x-ume-on-behalf-of: <team id>`; the gateway charges, reports and enforces per team and department | **Replaced** (#580) by acting as an assigned key (§4): A's unit (a key per assistant) without A's secrets, and B's mechanism (a header on the platform's own key) without team lists |
| **C. Ume.Rag meters itself** | tokens and cost of every model call recorded per assistant, knowledge base, step and client | **First** (stage 1, §5): finer than the gateway, reporting only (no hard stop), with the gateway's cost where it gives it |
| D. A free tag on gateway usage records | e.g. the assistant id in a header stored on the usage record | **No.** Reporting only, and B with C covers it; the run headers of gateway-changes §7.1 stay opaque run ids |

## 4. Target model (stage 2: each object on its own gateway key)

### 4.1 Who pays for which call

The attribution unit is a **gateway key**: the assigned key an assistant or knowledge base runs on (its `GatewayKeyId`,
data-model §2.1). The key's team and department (cost centre) follow from the key in the gateway, so the gateway's
reports, budgets and alerts show each assistant and knowledge base as a key of its own department.

| Call | Deployable | Runs as |
|---|---|---|
| Chat answer, condense, long-question condense, follow-up suggestions, query embedding, rerank | api | the assistant's key |
| Assistant test panel; answer-level evaluation and assistant test questions (P2) | admin | the assistant's key |
| Query embedding for a caller without an assistant (integration keys, token callers, MCP) | api | the first searched knowledge base's key |
| Retrieval test (admin §4.9), preview, preflight | admin, worker | the knowledge base's key |
| Ingestion embeddings, reprocessing, re-embedding | worker | the knowledge base's key |
| Evaluation: question generation, judge, three-way runs | worker | the knowledge base's key |
| Model check on save (`GET /v1/models`, admin §4.6 V8, V39) | admin | the object's key (no cost) |
| Organisation, key and model sync, embedding canary, embedding profile test | admin | the credential key itself (the integration's owner team) |
| Any call of an object without a key, and every call before the gateway's `features` contain `act-as-key` | any | the service's credential key (the integration's owner team) |

An assistant that searches another department's shared knowledge base pays for its own questions, its query
embeddings included; the knowledge base's key pays for keeping it indexed.

### 4.2 Rules

- **A key per object.** An assistant and a knowledge base each name an assigned key of their own department, chosen in
  the admin from the mirrored keys (admin §5.5.2, §5.5.4). Department admins make and manage the key in the gateway;
  Ume.Rag never creates a key and stores only its id. `OwnerTeamId` keeps one meaning, the edit scope.
- **The key must serve the object** (admin §4.6 V39): an active assigned key of the object's department that allows
  the object's residency ceiling and every model the object uses. For an assistant that is the answer model, the
  condense model, the embedding profile model of each linked knowledge base and the rerank model, because every call
  of a turn runs as its key; for a knowledge base the embedding profile model and the evaluation and judge models.
- **Without a key**, an object's calls run as the service's credential key and are charged to the integration's owner
  team. Once the gateway's `features` contain `act-as-key`, publishing an assistant needs a key; a knowledge base
  without one keeps indexing on the credential key and its page says so.
- **Per-assistant budgets** are the key's budget in the gateway, set by the people responsible for the money. No
  Ume.Rag setting holds a budget; the admin shows the status read-only with a link to the gateway (`admin_url`).
- **Every acted call** sends `x-ume-on-behalf-of: <key id>` and checks the echo; a missing or different echo fails
  closed (`Gateway:RequireDelegationEcho`, identity §4.7; security event 5011).
- **A spent budget or an unusable key** (gateway `402 budget_exceeded` naming the scope, `403` with the key's code or
  `delegation_not_allowed`, or the mirror showing the key not active or gone) is shown to the chat user as the
  assistant being unavailable for now (chat §3.6), counted in the daily totals, and raised as an attention item for the
  object's department admins with the reason (admin §5.5.1); a worker job waits and retries after an hour, it does
  not fail the document until its attempts run out (engine §7.3).
- **The key's settings are read-only in Ume.Rag**: models, residencies, PII policy, content guard, rate limits and the
  output cap are shown on the object's page and changed only in the gateway.

## 5. Stage 1: Ume.Rag's own metering

Reporting only. Ume.Rag records tokens and cost of every model call per assistant, knowledge base, step and client, and
takes the cost from the gateway wherever the gateway gives it: `x-ume-cost-usd` on complete answers, the stream comment
for streamed answers (`x-ume-stream-cost: 1`, gateway phase 2), and the Integration API's usage by run id (phase 3) for
calls that carry neither, such as an aborted stream. Only a call without any of these is estimated from the mirror's
prices (cached input at its own price) and marked as an estimate. The gateway stays the record for charging (it also
counts fallbacks and retries the caller does not see); Ume.Rag's split per step joins it on `x-ume-run-id` and
`x-ume-step` (gateway-changes §7.1).

| # | Step | Owner | Design to update first |
|---|---|---|---|
| C1 | Chat records tokens **and cost** of every model call per day, assistant and client: the answer (as today) plus condense, long-question condense, follow-ups, rerank and the query embedding; cost from `x-ume-cost-usd` or the stream comment, else estimated from the mirror's prices | b | chat §8.3, §17, data-model §2 (`AssistantDailyTotals`; a per-step split if the totals row is not enough) |
| C2 | The test panel's calls counted the same way, under the admin's own client row | b | admin §4.9 |
| C3 | Worker: ingestion and evaluation cost per knowledge base per day (from `RunItems.CostUsd` and the evaluation runs, filled from `x-ume-cost-usd`), checked to be filled for every model call | a | processing §11, §14; engine §10 |
| C4 | Admin API: cost report per assistant, knowledge base and department, by day or month, with CSV; department admins see their own departments, platform admins all | b | admin §8 (a new subsection) |
| C5 | Admin UI: cost on the assistant page and the knowledge base page, and a cost view per department | b | admin §5.5, §8 |
| C6 | Runbook: how to read the figures beside the gateway's | a | `docs/runbook.md` |

**Done when:** a day of chat on two assistants and one ingestion run show per assistant and knowledge base in the admin
UI and the CSV, and the totals agree with the gateway's usage for the same keys and day (fallbacks, retries and
estimated calls explained).

## 6. Stage 2: each object on its own gateway key

Each step starts when the gateway phase it needs is merged (gateway-changes §5; `work/main.md` M-12).

| # | Step | Gateway phase | Owner | Reference |
|---|---|---|---|---|
| B1 | The gateway's integration: credential and assigned keys, `GET /v1/ume/context`, the model facts on `GET /v1/models` (1); acting as an assigned key with the echo, run metadata, stream cost (2); the Integration API (3); the OpenAPI document of the contract | 1–3 | gateway (user) | gateway-changes §5, §7.1; gateway `docs/integrations.md` |
| B2 | One credential key per service in place of the per-level keys: the secret files, the residency handler (a ceiling above the service's maximum refused), the runbook | 1 | operator + gateway admin; b (api, admin) + a (worker) | identity §4.7; repo-and-deployment §6.3, §7.1 |
| B3 | The admin's sync from `/v1/ume/context` and `/v1/models` with `ETag`: the assigned keys mirror, the gateway's features, the models with their limits and route aliases (the manual model fields go); the FakeGateway's integration contract with a contract test | 1 | b (admin, FakeGateway) | admin §4.10; data-model §2.1; repo-and-deployment §4.3 |
| B4 | `GatewayKeyId` on assistants and knowledge bases; the key picker with the key's settings read-only and the gateway link; V39 | 1 | b + main (V-rule) | admin §4.6, §5.5.2, §5.5.4; data-model §2.1 |
| B5 | Every call of §4.1 sends `x-ume-on-behalf-of` and checks the echo; streamed calls ask for and read the stream cost; save checks ask `GET /v1/models` as the object's key; publishing an assistant needs a key | 2 | b (api, admin) + a (worker) | identity §4.7; chat §8; knowledge §5.2; engine §7.7 |
| B6 | The handling of §4.2 for a spent budget or an unusable key in chat, the admin and the worker; the department attention item | 2 | b + a | chat §3.6; engine §7.3; admin §5.5.1 |
| B7 | The gateway's cost and budget status per assistant and knowledge base from `/integration/v1` (`usage.read` on `rag-admin`'s credential key), beside stage 1's split | 3 | b | gateway-changes §5.6; admin §5.5.4 |

**Done when:** two departments' assistants and knowledge bases show as their own keys in the gateway's usage report, a
key budget stops its assistant with the planned message, and the per-level keys are revoked.

## 7. Open questions

| # | Question | Answer or default |
|---|---|---|
| Q1 | Billing granularity: department (cost centre) only, or finer where a department wants it? | **answered** (user, 2026-11-11, #580): per gateway key; a department gives an assistant or knowledge base its own key, and its team and department follow |
| Q2 | Who keeps the delegating keys' team lists current? | **answered** (#580): there are no team lists; department admins make assigned keys themselves |
| Q3 | Are hard budgets needed during the pilot? | open; default no: one department, stage 1 reports are enough. A key budget needs no Ume.Rag work once gateway phase 2 runs |
| Q4 | Currency in Ume.Rag's reports | **answered** (#580): USD, the gateway's cost |
| Q5 | Does the gateway bound acted requests by the credential key's `RequireMaxResidency` and `AllowedResidencies`? | open with the gateway owner (#580 review point 1); Ume.Rag does not rely on it (gateway-changes §5.3) |
| Q6 | Does `/v1/ume/context` list every active department with its member groups? | open with the gateway owner (to raise on #580); until then departments, teams and member groups come from the Data API (gateway-changes §5.5) |
