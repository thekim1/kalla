# Gateway changes: detailed design

Detail step 4 of the Ume.Rag design. Feature A (§5) is the gateway's integrations design (`docs/integrations.md` in
the gateway repository, decided by user, 2026-11-11, #580); this document keeps only what Ume.Rag relies on from it. Details [`../archive/area-plans/gateway-changes.md`](../archive/area-plans/gateway-changes.md) (archived: history, not binding; this design states every rule, the plan's answered questions in §15) and the
gateway side of [`../identity/design.md`](../identity/design.md) §1.4, §1.5, §1.7, §2 and §10 (feature C and the
tenant/guest refusal are designed there for both systems; this document only turns them into gateway work). The ADR
rows are in [`adr-drafts.md`](adr-drafts.md). Nothing here is implemented in the gateway yet (`~/git/llmgateway` is at
v0.8.5); Ume.Rag's side of feature B is built and runs against v0.8.5 until release 1 (§13.4).

**As built (#390, 2026-11-06):** release 1 is implemented in gateway draft PRs thekim1/llmgateway#65 (embeddings per
input), #66 (feature B, ADR 018) and #67 (FakeLlm, tool passthrough); rerank is #68 and the shared Keycloak #70. Where
this document and those PRs differ, the PRs win; §3.3, §3.5 and §7.4 below state the built behaviour. Ground rule: the
gateway never depends on Ume.Rag; every change is a generic gateway feature.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5), superseded by the PRs above. "Pipeline" means
`src/Ume.LlmGateway.Gateway/Pipeline/`; "Entities.cs" means `src/Ume.LlmGateway.Domain/Entities/Entities.cs`.

## 1. Overview

**Two releases**, each rolling-update safe; the gateway's own release practice decides the numbers (reasoning:
[weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)).
**Release 1, in P1b before the Ume.Rag pilot goes live**: feature B and the FakeLlm `dimensions` addition (with the
tool passthrough tests). **Later (P2): release 2**, before the first second-department or group audience: feature C.
Feature A (with the run metadata) follows the gateway's own integration phases 1–3 (§5). Until release 1, P1a runs against the gateway as it is (v0.8.5) on an interim
test/dev instance (§13.4). The letters name features:

| Feature | Content | Migration | RAG platform afterwards |
|---|---|---|---|
| **B** (release 1) | Residency ceiling `x-ume-max-residency`, key flag `RequireMaxResidency`, 403/503 rule (§3) | `KeyResidencyCeiling` | sends an explicit ceiling on every call (`External` for `public`), with the pilot team's per-level keys until release 2 (§13.4) |
| **C** (release 2) | Department member groups from the group claim, `/bff/user` `departmentSources` (§4); tenant and guest refusal for admin sign-in and Data API tokens (§4.6); `code` on catalog teams | `DepartmentMemberGroups` | syncs `memberGroups` and team codes; same group rule as the gateway; same `TenantId`/`RefuseGuests` |
| **A** (gateway integration phases 1–3) | Integration with credential and assigned keys, `GET /v1/ume/context` and the model facts on `GET /v1/models`; acting as an assigned key with `x-ume-on-behalf-of`; run metadata (§7.1); stream cost; Integration API (§5) | the gateway's own (`Integrations`, `UsageRunMetadata`) | one credential key per service; each assistant and knowledge base as its own assigned key (§13.4) |
| Development (release 1) | FakeLlm embedding dimensions (§6); tool passthrough tests (§7.2) | none | local development against a real gateway with FakeLlm (optional; the FakeGateway is the default) |

Not in either release's scope: the optional rerank endpoint (§7.4, thekim1/llmgateway#68), which shipped alongside
release 1's residency ceiling in gateway v0.9.0 (Foundry rerankers in v0.9.1).
It is pulled forward from P2 but is no go-live gate: with no rerank model configured Ume.Rag makes no rerank call
(Decisions "Rerank pulled forward").

## 2. Where the changes sit in the request pipeline

Current order in `GatewayRequestHandler.HandleAsync` (`GatewayRequestHandler.cs:56-244`), with the new steps:

| # | Step (current) | Citation | New |
|---|---|---|---|
| 1 | Authenticate key; status, owner active, address list | `GatewayRequestHandler.cs:64-71`, `GatewayRequestHandler.Stages.cs:21-77` | – |
| **1b** | – | – | **Read request headers** (§2.1): ceiling and `RequireMaxResidency` (B), run grouping (A). A malformed or missing required value is answered here, before the body is read. Acting as an assigned key (A) is the gateway's own stage after authentication (`docs/integrations.md`, §5) |
| 2 | Parse body, model name | `GatewayRequestHandler.cs:73-96` | – |
| 2b–c | Attachment policy, provider tools/storage (key) | `:98-115` | the assigned key's settings on an acted request (A, §5.3) |
| 3 | Resolve model; key model/provider allow-lists; endpoint | `:117-128`, `RouteResolver.cs:58-90` | – (the ceiling is not checked here: a routing rule may still send the request on-prem) |
| 4 | Rate limit (key) | `:130-140`, `GatewayRequestHandler.Stages.cs:83-116` | the assigned key's limit on an acted request (A) |
| 5 | PII guard → `restrictTo = OnPrem` | `:142-173` | – |
| 5b | Content guard (key) | `:175-180` | – |
| 6 | Routing rules (scoped by key lineage, team, department) | `:182-189`, `GatewayRequestHandler.Stages.cs:122-139`, `:361-415` | the assigned key's, its team's and department's on an acted request (A); rules can read the new headers like any header |
| 7 | Plan + candidate selection: provider list, residency, PII restriction, endpoint, circuit order, streaming, context window | `:191-201`, `GatewayRequestHandler.Stages.cs:169-205`, `RouteResolver.cs:143-188` | **ceiling intersected with the key's residencies and `restrictTo`**; 403/503 rule on the failure path (B) |
| 7b | Request policy (team + key) | `:203-213` | the assigned key's and its team's on an acted request (A) |
| 8 | Budget reservation (key lineage, team, department) | `:215-230`, `BudgetService.cs:56-67` | the assigned key, its team and department on an acted request (A) |
| 9–10 | Attempts with fallback and retry; accounting | `:232-243`, `:251-373`, `GatewayRequestHandler.Accounting.cs:24-134` | run metadata on the usage record (A, §7.1) |

The same steps run in `HandleRealtimeAsync` (`GatewayRequestHandler.Realtime.cs:24-110`) and, for authentication and
eligibility only, in `ListModelsAsync` (`GatewayRequestHandler.Models.cs:10-22`); step 1b is added to both (§3.4).

### 2.1 Request headers (new)

All are read once in step 1b by a new `GatewayRequestHeaders.Read(HttpRequest)` (`Pipeline/GatewayRequestHeaders.cs`)
that returns a `readonly record struct RequestDirectives(DataResidency? MaxResidency, string? RunId, string? Step,
GatewayError? Error)`; `x-ume-on-behalf-of` is read by the gateway's own integration stage (§5.2). Absent headers cost one `HeaderDictionary` lookup each and allocate nothing;
`StringValues` from Kestrel is used as is.

| Header | Feature | Value | Malformed → |
|---|---|---|---|
| `x-ume-max-residency` | B | `OnPrem`, `Eu` or `External`, case-insensitive (§3.1); required when the key has `RequireMaxResidency` | `400 invalid_request` (also when required and missing) |
| `x-ume-on-behalf-of` | A | assigned key id (GUID) (§5.2) | `400 invalid_request` |
| `x-ume-run-id`, `x-ume-step` | A | opaque metadata, trimmed, at most 100 characters kept, control characters replaced (§7.1) | never refused |

More than one value (repeated header or a comma-separated list) for the first two is `400 invalid_request`: two
ceilings or two keys have no safe meaning. No client header is forwarded to providers today except `OpenAI-Beta` on
live sessions (`GatewayRequestHandler.Realtime.cs:21-22`, `:139-140`), so none of these leaves the gateway.

Constants go into `GatewayHeaders` (`GatewayHeaders.cs:6-40`): `MaxResidency`, `OnBehalfOf`, `RunId`, `Step`.

## 3. Feature B: per-request residency ceiling

### 3.1 Header and parsing

- Values are the `DataResidency` names (`src/Ume.LlmGateway.Domain/Enums.cs:4-14`: `OnPrem = 0`, `Eu = 1`,
  `External = 2`), compared with `OrdinalIgnoreCase`, after trimming spaces. `EU`, `eu`, `onprem` all work.
- **No aliases.** `Any` is not accepted: omitting the header means "no ceiling", and `External` is the widest value.
  Numeric strings (`0`, `1`) are refused: `Enum.TryParse` would accept them, so the parser is a hand-written switch over
  the three names (`ResidencyCeiling.TryParse(ReadOnlySpan<char>, out DataResidency)` in
  `src/Ume.LlmGateway.Domain/Services/ResidencyCeiling.cs`).
- Empty value, unknown name, several values → `400 invalid_request`, message *"x-ume-max-residency must be one of
  OnPrem, Eu, External."* Ignoring a bad ceiling would fail open.
- The order of the enum is the order of the ceiling (`OnPrem < Eu < External`); the code relies on it, and a unit test
  pins it so a new enum member cannot silently change the meaning.
- The response header `x-ume-residency` keeps writing `Residency.ToString()` (`GatewayRequestHandler.Stages.cs:342`),
  i.e. `OnPrem`, `Eu`, `External`. Callers compare case-insensitively. The canonical spelling in the gateway docs and
  the RAG documents is `Eu`.
- **Key flag `RequireMaxResidency`** (`VirtualKey`, default `false`): a request from such a key without the header is
  refused in step 1b with `400 invalid_request`, *"This API key requires the x-ume-max-residency header (OnPrem, Eu or External)."* A caller
  whose code forgets the header then fails instead of running without a ceiling. Every Ume.Rag key sets it.

### 3.2 Semantics and placement

The ceiling is a hard constraint of candidate selection (step 7), exactly like the key's residency list and the PII
restriction:

- `RouteSelector.EffectiveResidencies(keyAllowed, restrictTo)` (`RouteSelector.cs:213-223`) gains a third parameter
  `DataResidency? ceiling`. Result = key's residencies (empty = all) ∩ `{r ≤ ceiling}` ∩ `{restrictTo}`; null still
  means "any". The existing two-argument overload stays for callers that have no ceiling.
- `IRouteResolver.SelectCandidatesAsync` (`RouteResolver.cs:49-50`, `:143-188`) gains `DataResidency? ceiling` next to
  `restrictTo`; line 149 passes it on. `SelectRouteAsync` (`GatewayRequestHandler.Stages.cs:169-180`) passes the
  parsed value through. The circuit prefetch (`RouteResolver.cs:132-141`) is unchanged: it covers a superset.
- **After rules and fallback lists**: `router.Plan` (`GatewayRequestHandler.Stages.cs:173`, `RouteResolver.cs:92-130`)
  has already turned rules and fallbacks into the ordered model list when the constraint is applied to every target
  of every model (`RouteResolver.cs:162-174`), so neither a rule, a rule fallback, a route's lower-priority target nor
  the last-candidate retry (`GatewayRequestHandler.cs:302-314`) can reach a provider above the ceiling: all of them
  iterate only the filtered `Candidates`.
- It only narrows: with no key list and no PII restriction, `OnPrem` leaves on-prem targets, `Eu` on-prem and EU
  targets, `External` everything.

### 3.3 Errors and response headers

| Situation | Status, code | Message |
|---|---|---|
| Malformed, empty or repeated header | `400 invalid_request` | *"x-ume-max-residency must be one of OnPrem, Eu, External."* (whitespace trimmed; no aliases, no numbers) |
| Header missing with `RequireMaxResidency` | `400 invalid_request` | *"This API key requires the x-ume-max-residency header (OnPrem, Eu or External)."* |
| Candidates empty, ceiling set, and **no configured target** of the planned models is within the ceiling (after the key's residency and provider lists and the endpoint, whatever the targets' state) | `403 max_residency_not_met` (new code in `GatewayErrorCodes`, `GatewayErrors.cs:10-40`) | *"No provider for '<model>' is configured at or below the residency 'OnPrem' that the request allows."* |
| Candidates empty, ceiling set, and targets within the ceiling exist but are **drained or disabled** | `503 no_eligible_provider` + `Retry-After` | *"No provider for '<model>' within the residency 'OnPrem' is available right now. Try again later."* |
| Ceiling set, targets within it exist, and every attempt failed (circuit-open targets included) | unchanged `502`/`504 all_providers_failed`: the same failure answers the same with or without a ceiling. Ume.Rag retries with the **same** ceiling, never a wider one | unchanged |
| Candidates empty for other reasons (PII on-prem restriction, key lists, no ceiling) | unchanged `503 no_eligible_provider` (`GatewayErrors.cs:117-119`) | unchanged; the PII text wins when `restrictTo` is set, since `OnPrem` is inside every ceiling |

The 403/503 test runs only on the failure path: `SelectCandidatesAsync` re-checks the plan's targets against the
ceiling and the key's lists, ignoring enabled/drained state, when the list comes out empty. `Retry-After` is
added only to the ceiling 503, not to other `no_eligible_provider` answers; it is
`CircuitBreaker:BreakDuration` in seconds (30 s default, `src/Ume.LlmGateway.Infrastructure/Stores/Stores.cs:203`).
403 is reserved for "this can never work" and matches the existing "key may not use any provider that offers …"
(`GatewayErrors.cs:111-112`); a drained or broken in-ceiling provider is temporary, so the caller waits and never
retries wider. The RAG FakeGateway behaves the same (`../platform/repo-and-deployment.md` §4.3).

Error bodies use the existing shapes (`GatewayErrors.cs:210-235`): OpenAI style, Anthropic style on `/v1/messages`,
Realtime `error` events on live sessions. The refusal is accounted like every refusal (`RejectAsync`,
`GatewayRequestHandler.Stages.cs:294-298`), with `ErrorCode = max_residency_not_met`. It is **not** a security event:
the client restricted itself.

Response headers: none new. `x-ume-residency` (`GatewayHeaders.cs:15`, set in `SetRoutingHeaders`,
`GatewayRequestHandler.Stages.cs:338-344`) reports the answering provider and is documented in `docs/admin-api.md`,
whose header list (`docs/admin-api.md:382`) misses it today. A caller that must be sure checks it before using the
answer: during the rolling update an older instance ignores the request header.

### 3.4 Interactions

| With | Behaviour |
|---|---|
| Routing rules | Rules run first and may choose an on-prem model for a ceiling request; conditions see the header like any header (`GatewayRequestHandler.Stages.cs:363-371`), e.g. `headers["x-ume-max-residency"] == "OnPrem"` (value as the client sent it). A matched rule whose targets are all above the ceiling ends in `max_residency_not_met`: no return to the requested model (ADR 013). |
| PII on-prem restriction | Intersected; `OnPrem` is within every ceiling, so the PII restriction alone decides when both are set. |
| Key residency list | Intersected. A key limited to `Eu` with ceiling `OnPrem` has no eligible residency → `max_residency_not_met`. |
| Fallback, retry, circuits | Only filtered candidates are attempted; open circuits still go last (`RouteResolver.cs:176-180`) but never return a removed target; all attempts failing → `502`/`504` as without a ceiling (§3.3). |
| Admission (rate limit and budget in one Redis call before routing, v0.8.9 `IAdmissionStore`) | The ceiling applies there too: a token quota for a residency above the ceiling is not reserved. |
| Streaming | The streaming filter (`RouteResolver.cs:181`) runs after the ceiling; the decision is made before any byte is sent, so a streamed answer never starts above the ceiling. |
| Token quotas per residency | Computed from the filtered candidates (`GatewayRequestHandler.cs:224-226`), so quotas follow automatically. |
| Session affinity | Unchanged; rendezvous runs over the filtered pool. |
| Context-window filter | Runs after selection (`GatewayRequestHandler.Stages.cs:187-199`). |

| Endpoint | Applies | Note |
|---|---|---|
| `/v1/chat/completions`, `/v1/responses`, `/v1/messages`, `/v1/messages/count_tokens` | yes | translated paths (Anthropic, Responses bridge) are chosen per target after selection |
| `/v1/embeddings` | yes | Ume.Rag sends its embedding profile's ceiling: `Eu` for the pilot's EU-hosted profile until on-prem hardware exists (register M2), `OnPrem` for an on-prem profile |
| `/v1/audio/transcriptions`, `/v1/audio/translations`, `/v1/images/generations`, `/v1/images/edits` | yes | same `SelectRouteAsync` path |
| `/v1/realtime`, `/v1/realtime/translations` | yes | read from the WebSocket upgrade request; passed at `GatewayRequestHandler.Realtime.cs:82`; the client-chosen input transcription model is checked with the same set (`:215`) |
| `GET /v1/models` | yes | **Today** it lists the routes and deployments allowed for the key (`IsModelAllowed`, `IsAllowedFor`) that have a target eligible under the key's own `AllowedResidencies` and `AllowedProviders` (`EffectiveResidencies(key.AllowedResidencies, null)`, `GatewayRequestHandler.Models.cs:22`) on an enabled deployment of an enabled, not drained provider (`RouteSelector.cs:186-200`, `Entities.cs:276`); `ume.residencies` lists those targets' residencies (`:44`); there is no request-side filter. **The release adds** the request's ceiling to that intersection (the `ceiling` overload at `:22`) for every key, delegating keys included, so a caller can ask "what may I use at `OnPrem`" and `ume.residencies` shows only residencies within the ceiling; `RequireMaxResidency` applies here too; `x-ume-on-behalf-of` is validated but does not change the list (§5.11). A model whose in-ceiling targets are all drained is not listed (as today), whereas a chat call to it answers 503. Ume.Rag validates an assistant's model this way on save (identity design §4.7, admin design §4.6 V8) |

### 3.5 Code changes

**As built (#66)** the placement differs from the table: no `GatewayRequestHeaders`/`RequestDirectives` struct yet
(release 2 if needed); `ResidencyCeiling` is in `Domain/Routing`, the header is read by
`RequestStages.ReadMaxResidency`, the intersection is a 3-argument `RouteSelector.EffectiveResidencies` overload, and
`requireMaxResidency` is on the admin API key only (not on the Data API feeds). The table is the original plan.

| File | Change |
|---|---|
| `src/Ume.LlmGateway.Domain/Services/ResidencyCeiling.cs` (new) | `TryParse`; `Mask(DataResidency)`; eight precomputed `DataResidency[]` (one per subset) so intersections never allocate |
| `src/Ume.LlmGateway.Domain/Services/RouteSelector.cs:213-223` | overload with `ceiling`; bit-mask intersection, returns a precomputed array or null |
| `src/Ume.LlmGateway.Domain/Entities/Entities.cs:158-166` | `IndexAllowLists` also stores `AllowedResidencyMask` (private field like the model index) |
| `Entities.cs:46-116` (`VirtualKey`) | `public bool RequireMaxResidency { get; set; }` (column in migration `KeyResidencyCeiling`, release 1, §8.1); copied by `KeyRotation.Rotate` (`KeyRotation.cs:64-87`, an explicit field list) |
| `src/Ume.LlmGateway.AdminApi/KeyEndpoints.cs:11-56` (`KeyRequest`), `KeyDto`, key form | `requireMaxResidency`; a checkbox *Require residency header* on the key form; on a credential key only gateway admins change it (§5.1) |
| `Pipeline/GatewayRequestHeaders.cs` (new) | §2.1 |
| `Pipeline/RouteResolver.cs:49-50`, `:143-188` | `ceiling` parameter; 403/503 rule on the failure path (§3.3) |
| `Pipeline/GatewayRequestHandler.cs`, `.Stages.cs`, `.Realtime.cs`, `.Models.cs` | step 1b; pass the ceiling; 503 + `Retry-After` when every attempted candidate was circuit-open (`:251-373`) |
| `Pipeline/GatewayErrors.cs` | `GatewayErrorCodes.MaxResidencyNotMet`, `GatewayError.InvalidHeader(name, expected)`, `GatewayError.MaxResidencyMissing`, `GatewayError.MaxResidencyNotMet(model, ceiling)`, `GatewayError.CeilingTargetsUnavailable(model, ceiling)` |
| `Pipeline/GatewayHeaders.cs` | `MaxResidency` |

### 3.6 Tests

| Test | Project |
|---|---|
| Parser: the three names in any case, spaces trimmed; `Any`, `0`, `EU-West`, empty, two values refused | `Domain.Tests/RouteSelectorTests.cs` (new region) |
| Enum order pinned (`OnPrem < Eu < External`) | `Domain.Tests` |
| `EffectiveResidencies` truth table: key list × ceiling × `restrictTo` (27 cases), never wider than any input, no allocation for the 3-argument path (`GC.GetAllocatedBytesForCurrentThread`) | `Domain.Tests` |
| Route with on-prem priority 2 and EU priority 1: ceiling `OnPrem` attempts only on-prem; EU never called even when on-prem fails (stub counts calls) | `Gateway.Tests/DataPlaneTests.cs` |
| Fallback chain and last-candidate retry never exceed the ceiling | `Gateway.Tests` |
| Routing rule sending to an EU model with ceiling `OnPrem` → 403 `max_residency_not_met`; rule on `headers["x-ume-max-residency"]` routes to on-prem | `Gateway.Tests/RoutingRuleGatewayTests.cs` |
| On-prem target drained, EU target up, ceiling `OnPrem` → 503 `no_eligible_provider` with `Retry-After`; on-prem circuit open and its attempt failing → 503 with `Retry-After`; no on-prem target configured → 403 | `Gateway.Tests/DataPlaneTests.cs` |
| `RequireMaxResidency`: header missing → 400 on every endpoint incl. `/v1/models` and the realtime upgrade; present → served; flag copied on rotation | `Gateway.Tests`, `Domain.Tests` |
| PII reroute + ceiling `Eu` → on-prem; PII reroute with no on-prem target → 503 with the PII message | `Gateway.Tests` |
| Streaming: 403 before any SSE byte; `x-ume-residency` on success | `Gateway.Tests` |
| Every endpoint in the table of §3.4 honours it (theory over `GatewayEndpoints.All`) incl. `/v1/models` listing and realtime upgrade | `Gateway.Tests`, `RealtimeTests.cs` |
| `/v1/models` with an `[OnPrem, Eu]` key: ceiling `OnPrem` lists only models with an on-prem target and `ume.residencies = [OnPrem]`; no header on a `RequireMaxResidency` key → 400 | `Gateway.Tests` |
| Malformed header → 400 in OpenAI and Anthropic shapes; accounted with `invalid_request` | `Gateway.Tests` |
| Token quota limited to `External` not reserved when the ceiling is `Eu` | `Gateway.Tests/TokenQuotaTests.cs` |

## 4. Feature C: department scope from member groups, and tenant/guest refusal

**Later (P2):** release 2. Designed in `../identity/design.md` §2 (both systems) and §1.7; this section is the
gateway's file-level plan for the work items of §2.7 there, in **claims-only** mode: only the token's group claim is
read. Alignment notes are in §4.10.

### 4.1 Data model

- `Department` (`Entities.cs:8-19`) gains `public List<string> MemberGroups { get; set; } = [];`
- Migration `DepartmentMemberGroups`: `ALTER TABLE "Departments" ADD "MemberGroups" varchar(256)[] NOT NULL DEFAULT
  '{}'`. Additive; the previous version ignores the column. **No GIN index** (§4.10).
- Grants: `Departments` is granted whole-table to all three roles (`deploy/postgres/app-roles.sql:17`, `:25`, `:47`),
  so no change.
- Validation (`DepartmentRequest`, `src/Ume.LlmGateway.AdminApi/OrganisationEndpoints.cs:10-13`): optional
  `List<string>? MemberGroups` (omitted = unchanged, `[]` clears), at most 50, each 1–256 characters after trimming, no
  control characters, duplicates removed case-insensitively.

### 4.2 Admin API and UI

| Item | Change |
|---|---|
| `PUT /api/departments/{id}` (`OrganisationEndpoints.cs:59-67`, already gateway-admin only) | takes `memberGroups`; audit `Update Department` with `DepartmentAudit` (`src/Ume.LlmGateway.AdminApi/AuditSnapshots.cs:65-67`) gaining `MemberGroups`; invalidation unchanged (`Config` + `Keys`, `:65`) |
| `DepartmentDto` (`src/Ume.LlmGateway.AdminApi/Contracts.cs:18`) | `memberGroups` (visible to every role that reads departments: department admins and viewers read their own) |
| `src/admin-ui/src/components/org/DepartmentFormDrawer.vue` | *Member groups* field: chips, free text with the hint "Values of the group claim: Entra object ids, AD FS or Keycloak group names" |
| `DepartmentDetail.vue` | lists member groups |

### 4.3 Sign-in resolution

New in `src/Ume.LlmGateway.AdminApi/Identity/` (same shape as the RAG copy, identity design §2.7 last paragraph):

- `DepartmentMembershipResolver.Resolve(ClaimsPrincipal, IReadOnlyList<DepartmentGroups>, OidcOptions) →
  MembershipResult(Codes, Sources)` implementing steps 1–4 of identity design §2.3. `DepartmentGroups` = (cost-centre
  code, member groups) of **active** departments with at least one member group, loaded in one query per sign-in
  (sign-ins are rare; no cache).
- Called from `OnTokenValidated` (`src/Ume.LlmGateway.AdminApi/AdminAuthentication.cs:123-127`) right after
  `ApplyClaimMapping`, after the tenant/guest check (§4.6). The handler becomes async (the department query).
- Group codes are added as ordinary `departmentCodes` claims, so `AdminContext.Departments()`
  (`src/Ume.LlmGateway.AdminApi/AdminContext.cs:81-90`) and every scoped query are unchanged. Roles are never added.
- The sources are stored in the ticket's `AuthenticationProperties.Items` (`.ume.departmentSources` as compact JSON):
  `TokenValidatedContext.Properties` flows into the cookie sign-in, the ticket lives in Redis (`AdminSessions.cs`), and
  *Stay signed in* re-signs with the same properties (`AdminAuthentication.cs:217-228`), so nothing is recomputed on
  extension. Scope stays fixed at sign-in, as today.
- Member groups reach the claim only if the adopter's IdP emits them (Entra: `groupMembershipClaims`, identity design
  §2.5); a group the token does not show, or an overage token, adds nothing (scope narrows, never widens).

### 4.4 Graph lookup (Entra only)

**Later (P2):** the Graph `checkMemberGroups` lookup, after release 2 with data-plane audiences (§17). Until then
member groups come only from the token's group claim (§4.3).

### 4.5 `/bff/user` and events

- `/bff/user` (`AdminAuthentication.cs:188-208`) adds `departmentSources: [{ code, source: "claim" | "group" }]`, read
  from the ticket properties. No group ids.
- No new sign-in event attribute; refusals are §4.6.

### 4.6 Tenant and guest refusal (admin sign-in and Data API tokens)

**Admin sign-in.** In `OnTokenValidated`, before claim mapping (one rule in both systems, identity design §1.7):

1. `Oidc:TenantId` set ⇒ the `tid` claim must equal it (missing `tid` is refused too). Reason `wrong_tenant`.
2. `Oidc:RefuseGuests` (default `true`) ⇒ refused when `acct` is `1`, or when an `idp` claim is present and differs
   from both the issuer and `https://sts.windows.net/{tid}/`. Reason `guest_refused`. Keycloak and AD FS emit neither
   claim: no effect there.

A refusal calls `ctx.Fail(new SignInRefusedException(reason))`; a new `OnRemoteFailure` handler recognises it,
redirects to `/?signin=refused&reason=<reason>` and calls `HandleResponse()` (the default would throw and show a 500).
No session is created. The admin UI shows *"Your account cannot sign in here (guest or other organisation)."* Event
2008 `admin.signin.refused` (Warning): `Reason`, `Actor` (the token's `sub`), `SourceAddress` (as `AdminActor`
truncates it).

**Data API.** `AddJwtBearer` (`src/Ume.LlmGateway.DataApi/DataApiAuthentication.cs:38-47`) gets an
`OnTokenValidated` with the same two checks from `DataApi:TenantId` and `DataApi:RefuseGuests`. Client-credentials
tokens carry no `acct`/`idp`, so only delegated user tokens can trip the guest check. Refusal = `ctx.Fail(...)` (401)
and event 3003 `data.token.refused`: `Reason`, `Client` (`DataApiAuthentication.ClientId`, `:80-85`), at most once a
minute per (client, reason) with `Count` (a misconfigured job retries constantly). Data API keys (`ume-dk-`) are not
affected.

### 4.7 Data API

- `GET /v1/catalog/departments` (`src/Ume.LlmGateway.DataApi/DataEndpoints.cs:140-142`) adds `memberGroups`, and each
  department of `GET /v1/ume/context` adds `member_groups` under `organisation.read` (where Ume.Rag reads them, §5.5;
  the gateway owner's plan on #580, feature C not scheduled).
- `GET /v1/catalog/teams` (`:143-145`) adds `code`. **Gap found**: Ume.Rag's `Teams` mirror has a `Code` column
  (data-model §2.1) that the catalog does not deliver today. Not needed for acting as a key (§5.2), but cheap and useful
  for display; Ume.Rag's `OrgSync` reads it from release 2 on.
- `docs/data-access.md:162` (catalog row) updated.

### 4.8 Settings

| Env (`deploy/.env`) | Setting | Default | Service |
|---|---|---|---|
| `UME_OIDC_TENANT_ID` | `Oidc:TenantId` | – | admin API |
| `UME_OIDC_REFUSE_GUESTS` | `Oidc:RefuseGuests` | `true` | admin API |
| `UME_DATA_TENANT_ID` | `DataApi:TenantId` | `${UME_OIDC_TENANT_ID:-}` | Data API |
| `UME_DATA_REFUSE_GUESTS` | `DataApi:RefuseGuests` | `${UME_OIDC_REFUSE_GUESTS:-true}` | Data API |

Added to `deploy/compose.prod.yaml` (admin API block `:136-146`, Data API block `:181-184`; the nested default follows
`DataApi__Authority: ${UME_DATA_AUTHORITY:-${UME_OIDC_AUTHORITY}}` at `:181`), `deploy/compose.portainer.template.yaml`
(`:196-206`, `:241-244`) and the generated `compose.portainer.yaml`, the comment block in `deploy/init-deployment.sh`
(`:270-274`), and `deploy/kubernetes/example/kustomization.yaml` (commented examples).

### 4.9 Tests

| Test | Project |
|---|---|
| Identity design §2.8 unit cases (merge without duplicates, case-insensitive, inactive departments ignored, never a role, overage token adds no group codes) | `AdminApi.Tests/DepartmentMembershipTests.cs` (new) |
| Tenant/guest: `tid` mismatch, missing `tid` with `TenantId` set, `acct=1`, foreign `idp`, member `idp` equal to issuer passes, Keycloak token unaffected; redirect, no session, event 2008 | `AdminApi.Tests/OidcClaimMappingTests.cs` |
| `/bff/user` `departmentSources`; extension keeps the properties | `AdminApi.Tests/AdminSessionTests.cs` |
| Member groups CRUD, validation limits, gateway-admin only for edit, department admin reads, audit before/after | `AdminApi.Tests/AdminApiTests.cs` |
| Data API: `memberGroups`, team `code`; token with wrong `tid` or guest claims → 401 + 3003; keys unaffected | `DataApi.Tests/DataApiTests.cs` |
| Migration adds the column with default on a populated database | `IntegrationTests/MigrationServiceTests.cs` |
| Keycloak user `group-member` (no department attribute, group `IFO-medarbetare` = member group of 3000 *Individ- och familjeomsorgen*, `MigrationService/DevSeeder.cs:73`) sees department 3000 and `departmentSources` `group` | e2e Playwright (`tests/e2e/specs/journeys.spec.ts`), realm `dev/keycloak/ume-realm.json` gains the group, the user and a *Group Membership* mapper (claim `groups`, full path off) |
| Organisation drawer: member groups field, accessibility | e2e `accessibility.spec.ts`, `OrganisationView.spec.ts` (Vitest) |

### 4.10 Alignment with the identity design

1. **No GIN index** on member groups in either system: both read all active departments' groups at sign-in or sync
   and never query by group.
2. **Refusal is a redirect, not a bare 403**: in a browser OIDC callback a 403 body cannot be shown by the SPA; the RAG
   BFF does the same (identity design §1.7).
3. **Event ids**: the RAG platform logs its refusal as 5001 with a `Reason`; the gateway has no equivalent sign-in
   failure event, so it gets 2008 `admin.signin.refused` and 3003 `data.token.refused`.

## 5. Feature A: acting as an assigned key (gateway integrations)

Decided by user, 2026-11-11 (#580). The gateway-side source of truth is the gateway's own design, `docs/integrations.md`
in `~/git/llmgateway`, with its ADRs 020 and 021: phases 1–3, the credential bound, `organisation.read`, member
groups and the contract document are released in **gateway v0.10.0** (2026-11-12; #81–#85). Ume.Rag pins **v0.10.1** (#86: a refusal of the assigned key is always `403`, §5.7). The contract Ume.Rag builds against is the gateway's **`docs/openapi/integrations.json`** (OpenAPI 3.1:
`/v1/ume/context`, the `ume` facts on `/v1/models`, the request headers, `/integration/v1`), held to the real answers
by the gateway's tests and changed by addition only (fields, features, scopes, enum values); a client ignores what it
does not know. `/v1` is snake_case, `/integration/v1` camelCase. This section states only what Ume.Rag relies on; where it and the
gateway's document differ, the gateway's document wins and this section is a bug to report. Which call runs as which
key, and the order of Ume.Rag's work, are in [`../platform/cost-attribution.md`](../platform/cost-attribution.md) §4,
§6. The gateway delivers it in phases, each additive within `v1` and safe for a rolling update:

| Gateway phase | Content | Ume.Rag can then |
|---|---|---|
| 1 (v0.10.0, #81) | the integration, credential and assigned keys, scopes, the model output limit, the model facts on `GET /v1/models`, `GET /v1/ume/context` | run each service on one credential key; fill the organisation, the key picker and the model limits from the gateway (§5.5) |
| 2 (v0.10.0, #82; the credential bound in #84) | `x-ume-on-behalf-of` and its echo, event 1006, run metadata stored (§7.1), the stream cost | run each assistant and knowledge base as its own key (§5.2) |
| 3 (v0.10.0, #83) | the Integration API (`usage.read`) | show the gateway's cost and budget status per assistant and knowledge base (§5.6) |

### 5.1 Model

- The gateway holds an **integration** *Ume.Rag*, made by gateway admins, with an owner team. Its keys have one of two
  roles:
  - **Credential keys**: one per Ume.Rag service (`rag-api`, `rag-admin`, `rag-worker`), in the owner team, with a
    secret, `AllowedIpRanges`, an expiry and rotation. Their settings, scopes included, are changed by gateway admins
    only. A call without `x-ume-on-behalf-of` runs as the credential key itself and is charged to the owner team
    (the syncs, the canary, model checks without a key, and every call of an object without a key).
  - **Assigned keys**: ordinary keys that a department's or team's admins make in their own team and mark *used
    through: Ume.Rag*. One assistant or knowledge base runs on one; its models, residencies, PII policy, content guard,
    rate limits, output cap and budget are set there. An assigned key cannot authenticate by itself (`403
    integration_only`); its secret is never shown or kept, so there is nothing to copy or rotate per assistant.
- Ume.Rag stores the assigned key's **id** on the object (data-model `Assistants`, `Collections`: `GatewayKeyId`) and
  mirrors the assigned keys from the context (§5.5; data-model `GatewayKeys`). It holds no team lists, makes no team
  per assistant, and needs no new budget scope.

### 5.2 Header, resolution and echo

`x-ume-on-behalf-of: <assigned key id>` (a GUID) on a request authenticated by a credential key. The gateway
resolves it right after authenticating the credential key, before the body is read (gateway v0.10.0,
`src/Ume.LlmGateway.Gateway/Requests/RequestStages.cs:118-180`; the codes in `GatewayErrors.cs:61-79`):

| Case | Gateway answer |
|---|---|
| No header | the credential key itself, as any key |
| Header not one GUID, or repeated | `400 invalid_request` (nothing logged) |
| Caller not a credential key, the id not an assigned key of the same integration (unknown ids answer the same), or the two keys sharing no residency (§5.3) | `403 delegation_not_allowed` |
| Assigned key disabled, or its team or department inactive | `403 key_disabled` |
| Assigned key revoked or expired | `403 key_revoked` / `403 key_expired` (gateway v0.10.1, #86; v0.10.0 answered `401 key_expired` and, for a revoked key, `403 delegation_not_allowed`). A `401` is always the credential key's own refusal |
| Usable | the request runs as the assigned key; the answer carries `x-ume-on-behalf-of` with the applied id |

- Ume.Rag sends the key's id, never a name, and only once the latest context's `gateway.features` contains
  `act-as-key` (phase 2). Each service learns the features from its own `GET /v1/ume/context` with its credential key
  (`If-None-Match`, at start and every 5 minutes; not accounted, no rate limit; identity §4.7), so no service depends
  on the admin's sync for it.
- **The echo** is the assigned key's id in the GUID's `D` form (lower case, `RequestStages.cs:132`); Ume.Rag parses it
  and compares GUIDs. It is set once acting is resolved, so it is on every later answer of that request: `2xx`, a
  `304` of `/v1/models`, and the errors after authentication (`402`, `429`, `400`, `5xx`); a refusal of the table
  above carries none. Ume.Rag checks it on every `2xx` and `304` answer to a request that sent the header: a missing or
  different echo fails closed before anything is used (`Gateway:RequireDelegationEcho`, default `true`; identity §4.7,
  event 5011), because an older gateway instance during a rolling update ignores the header and would charge the
  credential key. An error answer is handled by its code (§5.7), with or without an echo.
- Every endpoint that reaches a model honours the header, and so do `GET /v1/models` (the assigned key's models,
  §5.5) and `GET /v1/ume/context` (answered as if the assigned key had been presented; Ume.Rag never sends the header
  there).

### 5.3 What applies to a request made as an assigned key

| From the credential key and the request | From the assigned key |
|---|---|
| the client address list; the ceiling header `x-ume-max-residency` (§3), which still narrows every request | models, providers, residencies, PII policy, attachments, provider tools and storage, content guard; rate, token and concurrency limits; the request policy of the key, its team and department; routing rules; budgets and token quotas; the usage record and security events (which log the credential key's id too) |

The assigned key's team's request policy applies, so a team system prompt would change Ume.Rag's prompts; the admin
warns when the chosen key has one (`has_system_prompt`, admin §5.5.4).

**Residency bound on acted requests (decided by user, 2026-11-12, #580; gateway phase 2).** The credential key's own
model list does not apply to a request made as an assigned key, but its residency settings do:

1. the credential key's `RequireMaxResidency` keeps applying to acted requests (`400` without the header);
2. the credential key's `AllowedResidencies` stays an **upper bound**: the effective residencies are the assigned
   key's intersected with the credential key's (the credential key's model list may stay out of it).

With both, the worker's `Eu`-capped credential key keeps the worker below `External` at the gateway too, and a stolen
credential reaches no more than its own residencies. As built (#84, feature `credential-residencies`): an empty list
means all, on either key; no residency in common answers `403 delegation_not_allowed` before the body is read (event
1006, reason `no_common_residency`); a model outside the bound answers `503 no_eligible_provider`, or `403
max_residency_not_met` when the ceiling header excludes it; `/v1/models` with `x-ume-on-behalf-of` lists only models
within both keys' residencies, so V8 and V39 see the bound. Ume.Rag's own ceiling header and `x-ume-residency` check stay
(identity §4.7); this is the gateway-side guard beside them. Until the gateway release with it is deployed, Ume.Rag
sends no `x-ume-on-behalf-of` (phase 2 items wait on it, `work/main.md` M-12).

### 5.4 Budgets, rate limits and cost

- **Budgets** are the assigned key's, its team's and its department's; a spent one answers `402 budget_exceeded`
  (`error.type` `insufficient_quota`). The scope is only in `error.message` ("The key's budget (USD 50 per month) is
  used up. …", "The team's …", "The department's …", or "… token quota …"; `GatewayErrors.cs:168-186`): there is no
  structured scope field, so Ume.Rag passes the message on (chat §8.2) and never parses it; which budget is spent comes
  from `/integration/v1/budgets` (`exhausted`, §5.6). Ume.Rag shows the assistant as unavailable with the reason (chat
  §3.6, admin §5.5.4) and a worker job waits (engine §7.3).
- **Rate limits** are the assigned key's, so each assistant and each knowledge base has its own; indexing and chat
  already hit different limits. Ume.Rag asks for no platform-wide cap (review point 4 on #580); the worker keeps its
  own embedding concurrency limit (processing §11).
- **Cost**: `x-ume-cost-usd` on complete JSON answers (eight decimals). A streamed call sends `x-ume-stream-cost: 1`
  (exactly `1`; any other value asks for nothing) and the gateway writes one Server-Sent Events comment
  `: ume-cost-usd=<usd>` (six decimals, USD) **before the closing `data: [DONE]`** of an OpenAI-style stream such as
  `/v1/chat/completions`, or after the last event of a stream without one (phase 2; `features` `stream-cost`;
  `GatewayRequestHandler.cs:445-521`). The figure is the one the usage record gets, from the provider's token counts
  when the stream reports them (Ume.Rag sends `stream_options.include_usage`), else from the gateway's estimate. An
  aborted or broken stream has none, and the gateway's usage record stays the record. rag-api reads the comment and
  never forwards it to its own clients (chat §8.3). Run metadata: §7.1. Read-back: §5.6.

### 5.5 Discovery: `GET /v1/ume/context` and `GET /v1/models`

Both are answered from the gateway's memory with an `ETag`; Ume.Rag polls with `If-None-Match` (usually `304`), and
neither is accounted as a model request. The admin's sync is admin §4.10.

- **`GET /v1/ume/context`** (phase 1, a credential key). What Ume.Rag reads: `gateway` (`version`, `admin_url`,
  `features`), `integration`, `key` (with `integration_scopes` and `allowed_residencies`, the credential key's own
  list), `departments` (`id`, `name`, `cost_center_code`, `is_active`), `teams` (`id`,
  `department_id`, `name`, `code`, `is_active`) and `assigned_keys` (`id`, `name`, `team_id`, `department_id`,
  `status`, `owner_active`, `expires_at`, `allowed_models`, `allowed_residencies`, `pii_policy`,
  `content_guard_policy`, `requests_per_minute`, `tokens_per_minute`, `max_concurrent_requests`, `max_output_tokens`,
  `has_system_prompt`, `usable`: false when the presenting credential key shares no residency with it). Revoked keys
  are not listed; disabled and expired ones are, with their `status`. Without a scope, `departments` and `teams` are
  those of the assigned keys plus the owner team; with **`organisation.read`** (feature `organisation`; gateway admins
  set it on `rag-admin`'s credential key only) they are every department and team, inactive ones included with
  `is_active: false`, and each department with its `member_groups` (feature `member-groups`, #85). The `ETag` changes
  when a department or team changes.
- **`GET /v1/models`** gains under `ume` (phase 1): `kind`, `residencies` (an alias) or `residency` (a model),
  `context_window`, `max_output_tokens`, `features` and `pricing` (`input_per_million_usd`,
  `cached_input_per_million_usd`, `output_per_million_usd`). For an alias the limits are the smallest of its targets.
  With `x-ume-on-behalf-of` (phase 2) it lists the assigned key's models within both keys' residencies (§5.3) and the
  ceiling header, which the call still needs (the credential key's `RequireMaxResidency`): the key's model picker and
  the save checks (admin §4.6 V8, V39). It answers the refusals and the echo of §5.2; the `ETag` is over the body, so
  each key has its own.
- **Settled (#580, 2026-11-12):** the admin reads departments and teams from the context with `organisation.read`
  (admin §4.10), and Ume.Rag holds no Data API key. Member groups come from the same context (v0.10.0; identity §2.1).

### 5.6 Integration API (phase 3)

`/integration/v1` on the gateway's **Admin API** (its own base URL, `Gateway:IntegrationApiUrl`), authenticated with
`Authorization: Bearer` and a credential key of an active integration that carries the scope `usage.read`: `401` for
any other key or none, `403` without the scope (gateway v0.10.0,
`src/Ume.LlmGateway.AdminApi/Integrations/IntegrationApiEndpoints.cs`, `IntegrationApiService.cs`). Only
`rag-admin`'s credential key gets the scope, so the keys that serve chat can read nothing. The key's status, expiry
and address list apply as on the gateway; its own rate limit per key, `Admin:IntegrationRequestsPerMinute` (60,
`429` beyond it); every read is the gateway's security event 2006 `admin.data.read`. Errors are problem details; names
are camelCase. Read-only: keys and budgets are never created through it; they are made in the gateway by the people
responsible for the money.

| Endpoint | Answer | Ume.Rag uses it for |
|---|---|---|
| `GET /integration/v1/usage?from=&to=&granularity=&groupBy=&keyId=&runId=` | `from`, `to` required inclusive dates (Europe/Stockholm days), at most 366 days, else `400`; `granularity` `day` (default) or `month` (widens `from` and `to` to whole months); `groupBy` any of `key`, `model`, `step`, comma-separated, default `key`, empty for one row per period; `keyId` one of the integration's keys (another's is `404`); `runId` a filter on the kept records only. Answer `{ from, to, granularity, groupBy[], items[] }`, each item `{ period, keyId, keyName, model, step, requests, successfulRequests, inputTokens, cachedInputTokens, outputTokens, costUsd }` (a dimension not grouped by is `null`; `model` is the requested name, an alias as sent; `step` is `null` in the monthly totals before the gateway's retention boundary, which a month granularity adds) | the gateway's requests, tokens and cost per assistant's and knowledge base's key, by day or month and step (admin §8.6) |
| `GET /integration/v1/budgets?keyId=` | `keyId` required (another integration's is `404`). Answer `{ keyId, items[] }`, the active budgets that apply as the gateway applies them (the key's own and its rotation predecessors', its team's, its department's), each `{ id, scope (VirtualKey, Team, Department), scopeId, unit (Usd, Tokens), limit, residency (or null), period, periodStart, periodEnd, spent (in the unit), percentUsed, exhausted }` | the budget status on the assistant and knowledge base pages and which budget a `402` hit (admin §4.5, §5.5.4) |

### 5.7 Errors and security events

| Gateway answer to a request with `x-ume-on-behalf-of` | Ume.Rag |
|---|---|
| `400 invalid_request` on the header | a bug in Ume.Rag: logged at Error, never retried |
| `403 delegation_not_allowed`, `403 key_disabled`, `403 key_revoked`, `403 key_expired` | the object's key is unusable; final for the call: chat answers the assistant as unavailable (chat §3.6), the worker job waits (engine §7.3), and the admin raises an attention item (admin §5.5.1) |
| `401` (any code) | the credential key itself failed, not the object's key: a configuration fault, handled as any `401` from the gateway (alert the operators); never an attention item on the object |
| `402 budget_exceeded` | as above, with the gateway's message (§5.4) |
| `429 rate_limited` | the assigned key's own limit: as any `429` (chat §3.6 passes `Retry-After` on; the worker waits for it, engine §7.3) |
| a `2xx` or `304` without the echo, or with another id | 5011 `rag.gateway.delegation_missing`, the call aborted before use (identity §10) |

The gateway logs its refusals (not the malformed header) as security event 1006 `gateway.delegation.refused`
(`Reason`: `not_credential`, `key_not_assigned`, `key_revoked`, `key_expired`, `key_disabled`, `owner_inactive`,
`no_common_residency`; `RequestId`, `OnBehalfOf` as sent, `Endpoint`, and the credential key's `KeyId`,
`KeyPrefix`, `TeamId`, `DepartmentId`; `SecurityEvents.cs:86-88`). They are not usage records.

## 6. FakeLlm: embedding dimensions (plan §3c)

`/v1/embeddings` (`src/Ume.LlmGateway.FakeLlm/Program.cs:93-116`) returns `Vector(text)`, 8 values from SHA-256 bytes
(`:496-500`). Change:

- **Dimensions** from, in order: the OpenAI `dimensions` parameter (1–4 096; outside → 400 like OpenAI); else a model
  name ending in `-<n>` (e.g. `fake-embed-1024`); else 8 with **exactly today's `Vector()`**, so existing tests and
  benchmarks see identical output. The suffix lets a local alias `bge-m3` point at FakeLlm without the caller sending
  `dimensions` (Ume.Rag never sends it to an on-prem BGE-M3 profile; it does send `dimensions: 1024` for the pilot's
  EU `text-embedding-3-large` profile, which the gateway passes through unchanged).
- **Similar texts get similar vectors**: feature hashing of lower-cased word unigrams and character trigrams with
  FNV-1a 64 (the same function as `RouteSelector.AffinityHash`, `RouteSelector.cs:100-110`), bucket = hash mod n, sign
  from one hash bit, then L2-normalised. Deterministic across processes and platforms.
- **`encoding_format: "base64"`**: little-endian float32 bytes, base64. Several OpenAI SDKs ask for base64 by default
  (to verify for the .NET SDK Ume.Rag uses); without it the fake would break those clients.
- The gateway passes `dimensions` through unchanged (JSON passthrough, ADR 006; no rewrite of it in `src/`).
- Tests: a FakeLlm unit test (new `tests/Ume.LlmGateway.Gateway.Tests/FakeLlmEmbeddingTests.cs` via
  `WebApplicationFactory` of FakeLlm, or a small `FakeLlm.Tests`): default unchanged, dimensions honoured, suffix,
  cosine(similar) > cosine(unrelated), base64 round trip.
- Ships in release 1: no schema, development only; it lets Ume.Rag developers run against a real local
  gateway (`../platform/repo-and-deployment.md` §4.4), next to the default FakeGateway.

## 7. Small additions (plan §3a)

### 7.1 Run grouping headers (feature A, gateway phase 2)

Stored from gateway phase 2 on (`features` `run-metadata`); Ume.Rag already sends them and an older gateway ignores
them.

- `x-ume-run-id` and `x-ume-step`: any key may send them, acting or not; request headers only, never echoed on the
  answer (`RequestStages.cs:182-199`). Trimmed; at most 100 characters kept (longer values are
  truncated, never refused: metadata must not fail a model call); control characters replaced
  (`SecurityEvents.Printable`, `SecurityEvents.cs:30-49`, which allocates nothing when there is nothing to replace).
- Stored on `UsageRecord` as `RunId varchar(100) NULL`, `Step varchar(100) NULL` (the gateway's migration
  `UsageRunMetadata`). Not in monthly totals (retention summarises without them). No index. At v0.10.1 the admin request list
  (`/api/usage/requests`) has no `runId` filter or run column: the run id and step show only in a request's detail
  (asked of the gateway owner on #580; the runbook matches per step and day meanwhile).
- Exposed on `/v1/usage/records` (`runId`, `step`), in the admin request detail and on the
  Integration API's `usage` (`groupBy=step` groups by it, `runId=` filters on it; there is no grouping by run, §5.6),
  where Ume.Rag sets its per-step split beside the gateway's.
- Documented as **opaque ids only**: no names, no content, no user ids (ADR 003). Ume.Rag sends GUIDs and its step names
  (`condense`, `chat-answer`, `query-embed`, `rerank`, `embed`, `eval-judge`, …; chat §8.3, engine §7.7).

### 7.2 Tool passthrough guarantee (tests only)

Tests asserting that `tools`, `tool_choice`, `parallel_tool_calls`, assistant `tool_calls` and `tool` messages reach the
provider unchanged and in order, and that tool-call answers (JSON and streamed deltas) come back unchanged:

| Path | Where it is translated | Test home |
|---|---|---|
| Chat Completions → OpenAI-compatible (passthrough) | `RequestRewriter` (model only) | `Gateway.Tests/DataPlaneTests.cs` with a recording stub |
| Chat Completions → Anthropic Messages | `Infrastructure/Providers/AnthropicTranslator.cs` (tools in scope, `:8-13`) | `ProviderUnitTests.cs` (extend) |
| Chat Completions → Responses bridge (gpt-5.6+, GPT-6) | `Infrastructure/Providers/ResponsesBridge.cs` | `ResponsesBridgeTests.cs` (extends `:60-95`) |
| `/v1/responses`, `/v1/messages` passthrough | none | `DataPlaneTests.cs` |
| Request policy cannot touch tools | `RequestPolicy.ProtectedParameters` (`RequestPolicy.cs:37-43`) | `Domain.Tests/RequestPolicyTests.cs` |

Order matters: a test with three tools and interleaved `tool` messages compares the upstream body's arrays element by
element. They guard the paths Ume.Rag depends on, next to the routing change of feature B.

### 7.3 Model facts on `GET /v1/models` (gateway integration phase 1)

The model limits and features Ume.Rag needs come from `GET /v1/models` under `ume` (§5.5), not from the Data API
catalogue: `context_window`, `max_output_tokens` (a new nullable `ModelDeployment.MaxOutputTokens` in the gateway),
`features` (tags incl. `tools`), `kind`, the residencies and `pricing`. The admin's sync fills
`GatewayModels.ContextWindow`, `MaxOutputTokens` and `SupportsTools` from them and the manual entries go (admin §4.10;
Decisions "Answer length from the model"); until the gateway sends them, a platform admin enters them on the RAG
mirror. Route aliases are listed on `GET /v1/models` with their targets' residencies (`ume.residencies`), so the sync
takes them from there too and no manual route rows remain.

### 7.4 Rerank endpoint (its own release after release 1; #390, thekim1/llmgateway#68)

Decided (user, 2026-11-05/06). **Optional and provider-neutral:** `POST /v1/rerank`, Cohere/Jina-compatible, served
by any provider with the *Rerank* capability and a model of kind *Rerank* (vLLM, TEI, Infinity, Cohere, Jina). Request
`{ model, query, documents: (string | { text })[], top_n?, return_documents? }`; the provider's answer passes through
unchanged (`results[]` with `index`, `relevance_score`, `document` when asked; `usage` when reported); only `model` is
rewritten. Billed per input token (`usage.prompt_tokens`/`total_tokens` or Cohere `meta.billed_units`); no
context-window check; request policies cannot change `query` or `documents`. Authentication, `x-ume-max-residency`,
`RequireMaxResidency`, budgets and usage work as on every endpoint. With no rerank model configured the gateway answers
404/400 like any unknown or wrong-kind model, and Ume.Rag falls back to unreranked retrieval.

**Provider until on-prem hardware exists (M2):** Berget AI (Sweden, EU), `bge-reranker-v2-m3` (the model planned
on-prem, so the move is a route change), about $0.10 per million input tokens (verify on Berget's price page); its
processor agreement goes into G1. Ume.Rag calls it with the `Eu` ceiling until on-prem. Berget's exact shape is
verified when the provider is set up. The FakeGateway serves `/v1/rerank` in this shape.

## 8. Data model, migrations, Redis

### 8.1 Migrations

| Feature | Migration | DDL | Compatible with the previous version because |
|---|---|---|---|
| C (release 2) | `DepartmentMemberGroups` | `Departments.MemberGroups varchar(256)[] NOT NULL DEFAULT '{}'` | additive with default; old code does not select it |
| B (release 1) | `KeyResidencyCeiling` | `VirtualKeys.RequireMaxResidency boolean NOT NULL DEFAULT false` | same; an old admin API updating a key leaves the column untouched |

Feature A's migrations (`Integrations`, `UsageRunMetadata`) and grants are the gateway's own (`docs/integrations.md`). The migration service re-applies `app-roles.sql` on every start
(`deploy/postgres/app-roles.sql:1-5`).

### 8.2 Redis

No new keys: feature B and C need none, and acting as an assigned key uses that key's existing budget and rate-limit
counters (gateway `docs/integrations.md`).

## 9. Settings

| Setting | Env | Feature | Default |
|---|---|---|---|
| `Oidc:TenantId` | `UME_OIDC_TENANT_ID` | C | – |
| `Oidc:RefuseGuests` | `UME_OIDC_REFUSE_GUESTS` | C | `true` |
| `DataApi:TenantId` | `UME_DATA_TENANT_ID` | C | `UME_OIDC_TENANT_ID` |
| `DataApi:RefuseGuests` | `UME_DATA_REFUSE_GUESTS` | C | `UME_OIDC_REFUSE_GUESTS` |

B needs no settings: the ceiling is per request and `RequireMaxResidency` per key. Feature A's settings (e.g.
`Gateway:AdminUrl` for `admin_url`) are the gateway's own. The run-header length limit (100) is a constant,
documented. `Retry-After` reuses `CircuitBreaker:BreakDuration` (§3.3).

## 10. Security events

Ids follow `SecurityEvents.cs` (1xxx gateway, 2xxx admin API, 3xxx Data API; 1004 is unused and stays unused).
Names and attributes are only added (`docs/data-access.md:58-62`).

| Id | Name | Feature | When | Attributes |
|---|---|---|---|---|
| 1006 | `gateway.delegation.refused` | A | §5.7 (the gateway's design) | `Reason`, `OnBehalfOf`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId` |
| 2008 | `admin.signin.refused` | C | tenant or guest refusal | `Reason` (`wrong_tenant`, `guest_refused`), `Actor`, `SourceAddress` |
| 3003 | `data.token.refused` | C | Data API token refused by tenant/guest check, once a minute per (client, reason) | `Reason`, `Client`, `Count` |

SIEM alerts to add to `docs/security-events.md:28-34`: any 2008 (a guest or foreign account was assigned to the
application). Alerts on 1006 are the gateway's integrations design's.

## 11. Performance and benchmarks

The gateway's rule: no Postgres on the request path with warm caches, 2 Redis round trips before the first byte, 1
after (`docs/performance.md:9-21`); allocation and call counts are the regression gates
(`docs/performance-improvement-plan.md:94-101`).

| Feature | Hot-path cost (warm caches) | Allocations | Round trips |
|---|---|---|---|
| B, header absent | 1 header lookup; 1 flag read (`RequireMaxResidency`) | 0 B | unchanged |
| B, header present | + span compare of ≤ 8 chars, bit-mask intersection, precomputed array | 0 B | unchanged |
| B, failure path | + re-check of the plan's targets (only when candidates come out empty) | small, failure path only | unchanged |
| A | one header lookup, one frozen-dictionary lookup when present (gateway `docs/integrations.md`, *Request path cost*) | – | unchanged |
| Run headers | 2 header lookups; `Printable` | 0 B unless a value is over 100 characters or has control characters | – |
| Usage record | + 2 COPY values in the background writer | – | – |
| C | admin sign-in only (1 query) | – | none on the data plane |

Expected added time per request: well under 1 µs, against 196 µs in-memory / 1.65 ms Redis for a chat completion
(`docs/performance.md:237`). Feature A's benchmarks are the gateway's integrations design's.

Benchmarks:

| Benchmark | Expectation |
|---|---|
| `PipelineBenchmarks` (chat, stream, embeddings; `benchmarks/Ume.LlmGateway.Benchmarks/PipelineBenchmarks.cs`) | unchanged within noise; allocated bytes unchanged (no headers sent) |
| `PipelineBenchmarks` new case *Chat completion, ceiling + run id* | ≤ +1 % time, ≤ +200 B allocated vs. plain chat (the larger usage record) |
| `RoutingBenchmarks.SelectCandidates` (`RoutingBenchmarks.cs:70`) + new case with ceiling | 0 B more than without |
| `audit` | every scenario: 0 Postgres on the request path, same Redis command count |
| `benchmarks/compare` | not rerun for this release unless `PipelineBenchmarks` moves; the 2026-10-27 baseline stays the published one |

Compatible with improvement-plan item 2 (one Redis round trip, `docs/performance-improvement-plan.md:56-70`).

## 12. Gateway docs to update

| Page | Feature | Change |
|---|---|---|
| `docs/admin-api.md` (data plane `:360-415`, keys `:95-125`, organisation `:73-94`) | B, C | request headers `x-ume-max-residency`, `x-ume-run-id`, `x-ume-step`; response headers incl. the missing `x-ume-residency`; codes `max_residency_not_met`, the 403/503 rule; `/v1/models` with a ceiling; `memberGroups`; `requireMaxResidency`; `/bff/user` `departmentSources` |
| `docs/routing-rules.md` (`:181-206`) | B | ceiling as a hard constraint after rules (*Rules never widen access*); example rule on the header |
| `docs/providers-and-models.md` | B | residency section: what callers can enforce per request, `RequireMaxResidency`, what `x-ume-residency` reports |
| `docs/identity-and-roles.md` (`:26-35`, Entra `:160-186`, check `:217-229`) | C | department scope from member groups in the group claim (how to make Entra emit them); tenant and guest refusal; check-the-setup steps for `departmentSources` |
| `docs/data-access.md` (events `:64-77`, permissions `:159-162`, aggregate rule `:237`) | C | events 2008, 3003; new fields |
| `docs/security-events.md` (`:20-46`) | C | SIEM alerts of §10; guests refused by default |
| `docs/runbook.md` (*Identity provider* `:68-121`, *Data API* `:342-372`) | C | member groups not applied (group claim missing or overage), tenant/guest settings |
| `docs/admin-ui.md` | C | Organisation member groups |
| `docs/performance.md` | B | new benchmark case |
| `docs/upgrade-guide.md`, `CHANGELOG.md` | all | §13 |
| `docs/adr.md` | B, C | rows 018, 019 (`adr-drafts.md`); 020 and 021 are the gateway's integrations ADRs |
| `docs/local-development.md` | development, C | FakeLlm dimensions; Keycloak `group-member` user |

Feature A's pages (`docs/integrations.md` and what it lists) are updated by the gateway's integration phases.
| `README.md` header list, if it repeats the response headers | B | as admin-api |

## 13. Release plan

### 13.1 Contents

Two releases, so the pilot waits only for the smallest change a production gateway that other teams use must take
(reasoning: [weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):

- **Release 1, in P1b before go-live** (built in a separate session in the gateway repo): §3 (feature B), §6 (FakeLlm `dimensions`), §7.2 (tool passthrough tests), ADR 018,
  the docs of §12 for B, and the upgrade-guide entry (§13.3).
- **Later (P2): release 2**, before the first second-department or group audience: §4 (feature C with the tenant/guest
  refusal), ADR 019, the remaining docs of §12, settings in compose, Portainer and Kubernetes examples, and the
  Keycloak realm.
- **Feature A** (§5, with §7.1) comes in the gateway's integration phases 1–3, each its own release, independent of
  release 2.

The optional rerank endpoint (§7.4) is in neither release's scope: it shipped in gateway v0.9.0 (thekim1/llmgateway#68),
pulled forward from P2, and is no go-live gate (Decisions "Rerank pulled forward").

### 13.2 Migrations and rolling update

**Migrations**: release 1 `KeyResidencyCeiling`; release 2 `DepartmentMemberGroups`. Feature A's are the gateway's
integration phases'. **Rolling update: yes** for both, with these rules in the upgrade guide:

1. Set `RequireMaxResidency` on a key only after every gateway instance runs release 1: an old instance ignores
   `x-ume-max-residency` (callers must check `x-ume-residency`). The same holds for `x-ume-on-behalf-of` in the
   integration phase 2: an old instance ignores it and sends no echo, which is why Ume.Rag checks the echo (§5.2).
2. Do not rotate a key with `RequireMaxResidency` until the admin API is fully upgraded: an old instance's rotation
   does not copy the flag.
3. Release 2: during the window an old admin API instance signs users in without group-derived scope and without the
   tenant/guest checks; sessions are fixed at sign-in, so affected admins sign in again after the rollout.

### 13.3 Upgrade-guide outline

**Release 1.** *Rolling update: yes* (rules above). New request header `x-ume-max-residency` and error code
`max_residency_not_met`; `503` with `Retry-After` for unavailable in-ceiling targets; `/v1/models` honours the
ceiling; `x-ume-residency` now documented; key flag `RequireMaxResidency`; FakeLlm takes `dimensions` /
`fake-embed-<n>` (development only). Nothing changes for clients that send no ceiling.

**Release 2.** *Rolling update: yes* (rules above). *Check before upgrading*: guests are now refused at sign-in by
default (`acct = 1` or a foreign `idp`): set `UME_OIDC_REFUSE_GUESTS=false` to keep admitting them; set
`UME_OIDC_TENANT_ID` on Entra. *Opt in*: member groups on *Organisation* (the IdP must emit them in the group claim).
New `/bff/user` field, Data API `memberGroups`, team `code`, events 2008/3003. The integration phases have their own
upgrade-guide entries (gateway `docs/integrations.md`).

### 13.4 Ume.Rag afterwards

CI and tests run on the FakeGateway (`../platform/repo-and-deployment.md` §4.3), which implements the release-1
contract and the integration contract of §5 (built against the gateway's published OpenAPI document).

**P1a, before release 1** (Decisions "P1a plan answers", "Interim gateway for P1a"): Ume.Rag calls the gateway as it
is (v0.8.5), on an interim **test/dev gateway instance** on a fixed host (HTTPS, public certificate) with EU-hosted
embedding and chat models; it is the interim J10, and the same host's Keycloak holds the shared realm `ume` (J11).
Address and keys live only in user secrets or `deploy/.env` and secret files, never in the repository. Everything is
EU-hosted until on-prem hardware exists (register M2): the pilot embedding profile is `text-embedding-3-large` at 1024
dimensions, requested with `dimensions`. Ume.Rag already behaves as for release 1: every call carries
`x-ume-max-residency` with the level's key and the answer's `x-ume-residency` is checked before a byte is used
(`ResidencyCeilingHandler`); v0.8.5 ignores the request header, so until release 1 the ceiling rests on the keys'
`AllowedResidencies` and that check. `GET /v1/models` is not yet filtered by the ceiling.

**With release 1, in P1b before go-live (until the gateway's integration phase 1):**

- **Keys per level of the pilot department's team** (plan §4): ordinary team keys `OnPrem`-only (`AllowedResidencies = [OnPrem]`), `Eu`-max (`[OnPrem, Eu]`) and open
  (all), each issued per deployable that needs it with `AllowedIpRanges` of that deployable's hosts (`Entities.cs:89`)
  and `RequireMaxResidency = true`. The worker never holds the open key. Which deployable holds which level is the
  platform plan's (§6). Every Ume.Rag call is charged to the pilot team. The team's admins can edit these keys (no
  delegation, so not gateway-admin only); the explicit ceiling and the `x-ume-residency` check still hold whatever
  they change. The gateway applies the team's request policy to non-delegated requests, so the pilot team keeps none.
  The worker's keys also carry the gateway's existing `MaxConcurrentRequests` cap (429 + `Retry-After`), so a
  misconfigured worker cannot starve chat (processing design §11).
- Every call carries `x-ume-max-residency` (`OnPrem`, `Eu` or `External` for `public`) and the run headers (stored
  from the integration phase 2 on; older gateways ignore them); no `x-ume-on-behalf-of`. The answer's `x-ume-residency` is verified.
- The admin's sync reads departments, teams and member groups from `GET /v1/ume/context` with its credential key and
  the scope `organisation.read` (§5.1; B-35). No Data API key is used.
- Saving an assistant calls `GET /v1/models` with the level's key and `x-ume-max-residency` = the level's ceiling,
  and refuses a model that is not listed (§3.4, admin design §4.6 V8).

**With the gateway's integration phases** (§5; cost-attribution §6 orders Ume.Rag's work):

- **Phase 1:** a gateway admin makes the integration *Ume.Rag* and one credential key per service (`rag-api`,
  `rag-admin`, `rag-worker`) in its owner team, each with `AllowedIpRanges` and `RequireMaxResidency = true`; the
  worker's has `AllowedResidencies = [OnPrem, Eu]` (an upper bound on every key it acts as, #84, §5.3);
  `rag-admin`'s holds the scope `organisation.read`. They replace the per-level keys, which are revoked. Every call still carries `x-ume-max-residency`; calls
  run as the credential key and are charged to the owner team. The admin syncs the gateway's features, the assigned
  keys, the models and (with `organisation.read`) every department and team with its member groups from
  `GET /v1/ume/context` and `GET /v1/models` (admin §4.10); the Data API key is revoked.
  Department admins make assigned keys, and editors choose them on assistants and knowledge bases.
- **Phase 2** (`features` `act-as-key`): every call of an assistant or knowledge base with a key sends
  `x-ume-on-behalf-of: <key id>` and checks the echo (identity §4.7); streamed calls ask for the stream cost; saves
  check models with `GET /v1/models` as the object's key (admin §4.6 V8, V39); publishing an assistant needs a key.
- **Phase 3:** `rag-admin`'s credential key gets `usage.read`; the admin shows the gateway's cost and budget status
  per assistant and knowledge base (§5.6).
- Member groups (v0.10.0, feature `member-groups`): `OrgSync` takes `member_groups` from the context's departments.
  Tenant and guest refusal (`TenantId`/`RefuseGuests`) is not built in the gateway yet.

## 14. Changes to other documents in this repo

What this design needs from the other documents of this repo, and they state:

1. **`../identity/*`**: two releases, claims-only feature C, one credential key per service, explicit ceiling incl.
   `External`, `Gateway:RequireDelegationEcho` (from the integration phase 2).
2. **`../archive/area-plans/chat-api.md`, `../chat-api/design.md`**: no attribution modes or linked keys; `External` for `public`;
   a `503` with `Retry-After` from the gateway is retryable, `403 max_residency_not_met` is not.
3. **`../archive/area-plans/platform.md`** §6–§7: keys per level and deployable (the pilot team's until the gateway's
   integration phase 1, then one credential key per service); no linked keys; two gateway releases.
4. **`../platform/data-model.md`** §2.1: no `GatewayKeyLinks`; no GIN index on `Departments.MemberGroups`;
   `Teams.Code` from the context's `teams[].code`.
5. **`../platform/repo-and-deployment.md`** §4.3 FakeGateway: `RequireMaxResidency` behaviour (400 without the
   header), the 403/503 rule with `Retry-After`; §4.4 FakeLlm `dimensions` with release 1.
6. **`../knowledge-api/design.md`**, **`../ingestion-worker/design-engine.md`**: acting as an assigned key from the
   integration phase 2; the worker never sends `External`.
7. **`../archive/decisions-log.md`** rows "Gateway changes" and "Weakness review 5": features, release
   split, key flags, 503 rule.
8. **`../to-verify.md`**: rows 82 and 86 (Graph) in P2; the rows of §16.
9. **`../platform/repo-and-deployment.md`** §4.3 FakeGateway: `/v1/models` filtered by `x-ume-max-residency`; a rerank
   fake on the path agreed for §7.4 (the FakeGateway serves `/v1/rerank` until then).

## 15. Answered questions (2026-10-29)

1. **Guests refused by default for every adopter**: yes, `Oidc:RefuseGuests` and `DataApi:RefuseGuests` default to
   `true`, with a prominent *Check before upgrading* in the upgrade guide (§13.3).
2. **Who may change a key that acts for others**: gateway admins only, for credential keys (their managers may rotate
   and revoke them); an assigned key is managed by its own team's and department's admins (§5.1).

## 16. To verify

- `TokenValidatedContext.Properties.Items` set in `OnTokenValidated` reach the cookie ticket and
  `CookieSigningInContext.Properties` (expected from the OIDC handler's sign-in, not checked in code).
- `ctx.Fail(...)` in `OnTokenValidated` + `OnRemoteFailure` with `HandleResponse()` gives a clean redirect on .NET 10.
- Entra `acct` and `idp` values for B2B guests and members in ID tokens and in delegated access tokens for the Data
  API; whether a member's ID token ever carries `idp` equal to the v1 issuer form.
- Docker Compose nested default `${A:-${B:-true}}` (one level is already used at `deploy/compose.prod.yaml:181`).
- The OpenAI .NET SDK's default `encoding_format` for embeddings (base64 or float).
- Ollama's OpenAI-compatible `/v1/embeddings` and a `dimensions` parameter (only matters if a caller sends it to real
  BGE-M3; Ume.Rag does not).
- Realtime: whether WebSocket clients used by adopters can set custom headers on the upgrade request (server SDKs can;
  browsers cannot, which only matters for feature B, not for Ume.Rag).
- The attempt loop can tell that every attempted candidate was circuit-open or cooling down at selection
  (the selection's `CircuitStatus`, `Stores.cs:175`, is available after `SelectCandidatesAsync`), for the 503 of §3.3.
- Entra emits member groups in the admin registration's group claim with the chosen `groupMembershipClaims`
  setting (identity design §2.5), and how often pilot admins hit the overage limit.
- (P1, before the FakeGateway fake is fixed): the rerank endpoint's path and request shape, and which
  on-prem runtime serves `bge-reranker-v2-m3` behind it (§7.4).

## 17. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../archive/passes/simplification-pass.md) §6–§7:

| Removed or deferred | Where it went |
|---|---|
| Releases B → C → A (three upgrade windows, per-release upgrade guides, ADR rows per release) | two releases: B in release 1, C in release 2; A in the gateway's integration phases (§5, §13, §18) |
| Graph `checkMemberGroups` lookup (`Oidc:GroupLookup`, `GraphTokenClient`, `IGroupMembershipSource`), `GroupMember.Read.All`, Redis `ume:groups:{oid}`, circuit, event 2007, `departmentLookup` on `/bff/user`, attribute `DepartmentLookup` on 2002, the "failed" banner, directory group search endpoints | P2, with data-plane audiences and approval package 2 (identity plan §2); P1 member groups are entered as group claim values |
| Effective team's request policy on delegated requests | replaced by acting as an assigned key: that key's and its team's request policy apply (§5.3) |
| `403 max_residency_not_met` while in-ceiling targets are only drained | `503 no_eligible_provider` + `Retry-After` (§3.3) |
| Ume.Rag interim: per-team linked keys `GatewayKeyLinks`, `onprem` keys, the "platform service key" (one key) | removed; FakeGateway in CI and tests, the interim test/dev gateway in P1a, the pilot team's keys per level and deployable until the gateway's integration phase 1, then one credential key per service and an assigned key per assistant and knowledge base (§13.4) |

## 18. Deferred or removed (weakness review)

Per [weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Removed or deferred | Where it went |
|---|---|
| One combined release before the pilot (B, C, A; ADRs 018–020 together) | release 1 (B, FakeLlm `dimensions`, tool passthrough tests, ADR 018) before the pilot; release 2 (C with tenant/guest refusal, ADR 019) in P2; A and the run headers in the gateway's integration phases (§5, §13) |
| `RequireMaxResidency` in migration `KeyDelegation` | its own migration `KeyResidencyCeiling` in release 1 (§8.1) |
| Ume.Rag on delegating keys from the pilot on | the pilot team's per-level keys until the gateway's integration phase 1 (§13.4) |
