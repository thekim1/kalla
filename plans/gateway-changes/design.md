# Gateway changes: detailed design

Detail step 4 of the Ume.Rag design. Details [`plan.md`](plan.md) (binding, incl. its answered questions) and the
gateway side of [`../identity/design.md`](../identity/design.md) §1.4, §1.5, §1.7, §2 and §10 (feature C and the
tenant/guest refusal are designed there for both systems; this document only turns them into gateway work). The ADR
rows are in [`adr-drafts.md`](adr-drafts.md). Nothing here is implemented in the gateway yet (`~/git/llmgateway` is at
v0.8.5); Ume.Rag's side of feature B is built and runs against v0.8.5 until release 1 (§13.4).

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5). "Pipeline" means
`src/Ume.LlmGateway.Gateway/Pipeline/`; "Entities.cs" means `src/Ume.LlmGateway.Domain/Entities/Entities.cs`.

## 1. Overview

**Two releases**, each rolling-update safe; the gateway's own release practice decides the numbers (reasoning:
[weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)).
**Release 1, in P1b before the Ume.Rag pilot goes live**: feature B and the FakeLlm `dimensions` addition (with the
tool passthrough tests). **Later (P2): release 2**, before the first second-department or group audience: features C
and A (with the run grouping headers). Until release 1, P1a runs against the gateway as it is (v0.8.5) on an interim
test/dev instance (§13.4). The letters name features:

| Feature | Content | Migration | RAG platform afterwards |
|---|---|---|---|
| **B** (release 1) | Residency ceiling `x-ume-max-residency`, key flag `RequireMaxResidency`, 403/503 rule (§3) | `KeyResidencyCeiling` | sends an explicit ceiling on every call (`External` for `public`), with the pilot team's per-level keys until release 2 (§13.4) |
| **C** (release 2) | Department member groups from the group claim, `/bff/user` `departmentSources` (§4); tenant and guest refusal for admin sign-in and Data API tokens (§4.6); `code` on catalog teams | `DepartmentMemberGroups` | syncs `memberGroups` and team codes; same group rule as the gateway; same `TenantId`/`RefuseGuests` |
| **A** (release 2) | Delegated attribution `x-ume-on-behalf-of`, key flag `ApplyTeamPolicy` (§5); run grouping headers (§7.1) | `KeyDelegation`, `UsageDelegationAndRuns` | per-level delegating keys per deployable (§13.4) |
| Development (release 1) | FakeLlm embedding dimensions (§6); tool passthrough tests (§7.2) | none | local development against a real gateway with FakeLlm (optional; the FakeGateway is the default) |

Not in either release: the rerank endpoint, a separate P2 change (§7.4).

## 2. Where the changes sit in the request pipeline

Current order in `GatewayRequestHandler.HandleAsync` (`GatewayRequestHandler.cs:56-244`), with the new steps:

| # | Step (current) | Citation | New |
|---|---|---|---|
| 1 | Authenticate key; status, owner active, address list | `GatewayRequestHandler.cs:64-71`, `GatewayRequestHandler.Stages.cs:21-77` | – |
| **1b** | – | – | **Read request headers** (§2.1): ceiling and `RequireMaxResidency` (B), delegation (A), run grouping (A). A malformed or missing required value or a refused delegation is answered here, before the body is read |
| 2 | Parse body, model name | `GatewayRequestHandler.cs:73-96` | – |
| 2b–c | Attachment policy, provider tools/storage (key) | `:98-115` | applies the delegating key's settings (A) |
| 3 | Resolve model; key model/provider allow-lists; endpoint | `:117-128`, `RouteResolver.cs:58-90` | – (the ceiling is not checked here: a routing rule may still send the request on-prem) |
| 4 | Rate limit (key) | `:130-140`, `GatewayRequestHandler.Stages.cs:83-116` | key's limit also under delegation (A) |
| 5 | PII guard → `restrictTo = OnPrem` | `:142-173` | – |
| 5b | Content guard (key) | `:175-180` | – |
| 6 | Routing rules (scoped by key lineage, team, department) | `:182-189`, `GatewayRequestHandler.Stages.cs:122-139`, `:361-415` | team and department = effective team's (A); rules can read the new headers like any header |
| 7 | Plan + candidate selection: provider list, residency, PII restriction, endpoint, circuit order, streaming, context window | `:191-201`, `GatewayRequestHandler.Stages.cs:169-205`, `RouteResolver.cs:143-188` | **ceiling intersected with the key's residencies and `restrictTo`**; 403/503 rule on the failure path (B) |
| 7b | Request policy (team + key) | `:203-213` | delegated request: the key's policy only, unless `ApplyTeamPolicy` (A) |
| 8 | Budget reservation (key lineage, team, department) | `:215-230`, `BudgetService.cs:56-67` | effective team and department (A) |
| 9–10 | Attempts with fallback and retry; accounting | `:232-243`, `:251-373`, `GatewayRequestHandler.Accounting.cs:24-134` | usage record fields (A) |

The same steps run in `HandleRealtimeAsync` (`GatewayRequestHandler.Realtime.cs:24-110`) and, for authentication and
eligibility only, in `ListModelsAsync` (`GatewayRequestHandler.Models.cs:10-22`); step 1b is added to both (§3.4,
§5.11).

### 2.1 Request headers (new)

All are read once in step 1b by a new `GatewayRequestHeaders.Read(HttpRequest)` (`Pipeline/GatewayRequestHeaders.cs`)
that returns a `readonly record struct RequestDirectives(DataResidency? MaxResidency, string? OnBehalfOf, string? RunId,
string? Step, GatewayError? Error)`. Absent headers cost one `HeaderDictionary` lookup each and allocate nothing;
`StringValues` from Kestrel is used as is.

| Header | Feature | Value | Malformed → |
|---|---|---|---|
| `x-ume-max-residency` | B | `OnPrem`, `Eu` or `External`, case-insensitive (§3.1); required when the key has `RequireMaxResidency` | `400 invalid_request` (also when required and missing) |
| `x-ume-on-behalf-of` | A | team id (GUID, `D` format) or team code, 1–100 characters, no control characters (§5.2) | `400 invalid_request` |
| `x-ume-run-id`, `x-ume-step` | A | opaque metadata, trimmed, at most 100 characters kept, control characters replaced (§7.1) | never refused |

More than one value (repeated header or a comma-separated list) for the first two is `400 invalid_request`: two
ceilings or two teams have no safe meaning. No client header is forwarded to providers today except `OpenAI-Beta` on
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
  refused in step 1b with `400 invalid_request`, *"This API key requires the x-ume-max-residency header."* A caller
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
| Malformed header, or missing with `RequireMaxResidency` | `400 invalid_request` | as §3.1 |
| Candidates empty, ceiling set, and **no configured target** of the planned models is within the ceiling (after the key's residency and provider lists and the endpoint, whatever the targets' state) | `403 max_residency_not_met` (new code in `GatewayErrorCodes`, `GatewayErrors.cs:10-40`) | *"No provider for '<model>' is configured at or below the residency 'OnPrem' that the request allows."* |
| Candidates empty, ceiling set, and targets within the ceiling exist but are **drained or disabled** | `503 no_eligible_provider` + `Retry-After` | *"No provider for '<model>' within the residency 'OnPrem' is available right now."* |
| Ceiling set, and every attempted candidate was **circuit-open or cooling down** at selection (they are not removed, they go last, `RouteResolver.cs:176-180`) and all attempts failed | `503 no_eligible_provider` + `Retry-After`, instead of `502`/`504` from `AllProvidersFailed` (`GatewayErrors.cs:161`) | as above |
| Candidates empty for other reasons (PII on-prem restriction, key lists, no ceiling) | unchanged `503 no_eligible_provider` (`GatewayErrors.cs:117-119`) | unchanged; the PII text wins when `restrictTo` is set, since `OnPrem` is inside every ceiling |

The 403/503 test runs only on the failure path: `SelectCandidatesAsync` re-checks the plan's targets against the
ceiling and the key's lists, ignoring enabled/drained state, when the list comes out empty. `Retry-After` is
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
| Fallback, retry, circuits | Only filtered candidates are attempted; open circuits still go last (`RouteResolver.cs:176-180`) but never return a removed target; all of them open and failing → 503 with `Retry-After` (§3.3). |
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

| File | Change |
|---|---|
| `src/Ume.LlmGateway.Domain/Services/ResidencyCeiling.cs` (new) | `TryParse`; `Mask(DataResidency)`; eight precomputed `DataResidency[]` (one per subset) so intersections never allocate |
| `src/Ume.LlmGateway.Domain/Services/RouteSelector.cs:213-223` | overload with `ceiling`; bit-mask intersection, returns a precomputed array or null |
| `src/Ume.LlmGateway.Domain/Entities/Entities.cs:158-166` | `IndexAllowLists` also stores `AllowedResidencyMask` (private field like the model index) |
| `Entities.cs:46-116` (`VirtualKey`) | `public bool RequireMaxResidency { get; set; }` (column in migration `KeyResidencyCeiling`, release 1, §8.1); copied by `KeyRotation.Rotate` (`KeyRotation.cs:64-87`, an explicit field list) |
| `src/Ume.LlmGateway.AdminApi/KeyEndpoints.cs:11-56` (`KeyRequest`), `KeyDto`, key form | `requireMaxResidency`; a checkbox *Require residency header* on the key form; on a delegating key only gateway admins change it (§5.7) |
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
| `/v1/models` with a delegating `[OnPrem, Eu]` key: ceiling `OnPrem` lists only models with an on-prem target and `ume.residencies = [OnPrem]`; no header on a `RequireMaxResidency` key → 400; `x-ume-on-behalf-of` of a listed team → same list | `Gateway.Tests` |
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

- `GET /v1/catalog/departments` (`src/Ume.LlmGateway.DataApi/DataEndpoints.cs:140-142`) adds `memberGroups`.
- `GET /v1/catalog/teams` (`:143-145`) adds `code`. **Gap found**: Ume.Rag's `Teams` mirror has a `Code` column
  (data-model §2.1) that the catalog does not deliver today. Not needed for delegation (§5.2), but cheap and useful
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

## 5. Feature A: delegated attribution

### 5.1 Model

**Later (P2):** release 2. Until then Ume.Rag uses the pilot team's per-level keys and sends no `x-ume-on-behalf-of`
(§13.4).

- `VirtualKey` (`Entities.cs:46-116`) gains `public List<Guid> DelegatedTeamIds { get; set; } = [];` (empty = not
  delegating). An array, like `AllowedModels`/`AllowedResidencies` (`:76-82`) and `RoutingRule.ScopeIds`: no join
  table, so the key loads, rotates and audits as one row.
- `VirtualKey` also gains `public bool ApplyTeamPolicy { get; set; }` (default `false`), read only on delegated
  requests (§5.3); `RequireMaxResidency` (§3.1) comes earlier, with release 1.
- Migration `KeyDelegation` (release 2): `ALTER TABLE "VirtualKeys" ADD "DelegatedTeamIds" uuid[] NOT NULL DEFAULT
  '{}'`, `ADD "ApplyTeamPolicy" boolean NOT NULL DEFAULT false`. No index (the only query by team is the rare delete guard, §5.8, over a small table).
- **Delegated views.** Following the existing pattern of read-only indexes on cached keys (`IndexAllowLists`,
  `Entities.cs:145-166`), `VirtualKey` gets, not mapped to the database (`[NotMapped]`/`Ignore` in
  `GatewayDbContext.cs:62-70`):
  - `public Team? OwnerTeam { get; private set; }` – null on a stored key; on a view, the key's real team;
  - private `FrozenDictionary<string, VirtualKey>` (team code, `Ordinal`) and `FrozenDictionary<Guid, VirtualKey>`;
  - `public void IndexDelegations(IReadOnlyList<Team> teams)` builds, once per cache load, one **view** per listed
    team: a shallow copy of the key (same `Id`, `Prefix`, allow-lists and their indexes, rate limits, policies) whose
    `Team`/`TeamId` are the listed team and `OwnerTeam` the real one;
  - `public VirtualKey? DelegateFor(string value)` – GUID (`Guid.TryParseExact(value, "D")`) or code lookup.
- A request for a team is then processed with the view as its key. Everything downstream that reads `key.Team`,
  `key.TeamId` or `key.Team.DepartmentId` (request policy, budgets, rules, accounting, security events) gets the
  effective team without changes; everything that reads the key's own settings or `key.Id` (allow-lists, PII,
  attachments, provider tools/storage, content guard, rate and concurrency limits, live-session slots, `LastUsedAt`,
  key budgets) still gets the delegating key's. One dictionary lookup per request, no allocation.

### 5.2 Header and resolution

- `x-ume-on-behalf-of: <team id | team code>`. A GUID in `D` format (any case) is looked up as a team id, anything else
  as a team code (exact, `Ordinal`, like the unique index on `Teams.Code`). 1–100 characters, no control characters,
  one value (§2.1).
- **Ume.Rag sends the team id** (its `Teams` mirror stores the gateway id, data-model §2.1). Team codes can be changed
  or cleared by department admins (`OrganisationEndpoints.cs:91-100`, `Code` optional, `Entities.cs:29-32`), so a code
  is a poor long-lived reference; ids never change. Codes stay accepted for hand-written clients.
- Resolution runs in step 1b, after authentication (the key's own status, owner team and address checks stay as they
  are, `GatewayRequestHandler.Stages.cs:53-74`):

| Case | Result |
|---|---|
| No header | the key as today, `Delegated = false` |
| Header, key has an empty list | `403 delegation_not_allowed`, reason `not_delegating` |
| Value matches no listed team (id or code) | 403, reason `team_not_listed` (also for teams that do not exist: the gateway does not reveal which; the event says `team_not_listed` either way, as the view dictionary only holds listed teams) |
| Listed team inactive, or its department inactive | 403, reason `team_inactive` (same code; message names the team as inactive) |
| Listed, active | `state.Key = view`; response header `x-ume-on-behalf-of: <team id>` |

  The refusal is answered with `RejectAsync` before the body is read and accounted against the key's own team with
  `ErrorCode = delegation_not_allowed`.
- **Response header** `x-ume-on-behalf-of` echoes the resolved team id on every answer after resolution (success and
  later refusals). A caller can verify that the gateway applied the delegation: an instance that predates the release
  ignores the request header and sends no echo (§13.2).

### 5.3 What applies to a delegated request

| Aspect | Source | Citation (current code that reads it) |
|---|---|---|
| Model allow-list, provider allow-list, residency allow-list | delegating key (teams have none, so the intersection is the key's lists) | `RouteResolver.cs:73-82`, `:149-155` |
| Residency ceiling (B) | request header, intersected | §3.2 |
| PII policy, attachment policy, provider tools/storage, content guard | delegating key | `GatewayRequestHandler.cs:98-180` |
| Rate limits, tokens per minute, concurrency, live-session slots | delegating key (overall cap) | `GatewayRequestHandler.Stages.cs:83-116`, `GatewayRequestHandler.Realtime.cs:89` |
| Request policy | **the delegating key's only**: `Merge(null, key)`; with `ApplyTeamPolicy`, `Merge(effective team, key)`. Neither the effective team's nor the platform team's policy applies by default | `GatewayRequestHandler.cs:205` (today `RequestPolicy.Merge(key.Team?.RequestPolicy, key.RequestPolicy)`, which on a view would merge the effective team's), `src/Ume.LlmGateway.Domain/Services/RequestPolicy.cs:46-65` |
| Routing rules | key-scoped rules of the delegating key's lineage, team and department rules of the **effective** team, global rules; `team_*`/`department_*` variables are the effective team's | `GatewayRequestHandler.Stages.cs:397-414` |
| Budgets and token quotas | delegating key's lineage + effective team + effective department (§5.4) | `BudgetService.cs:56-67` |
| Session affinity | scoped to the key id (unchanged) | `GatewayRequestHandler.Stages.cs:155` |

Delegation can never widen access: every allow-list is the key's; teams carry no allow-lists of their own
(`Entities.cs:21-40`); rules never widen (ADR 013). What changes is *whose* money and rules apply. The team's request
policy (system prompt, parameter defaults and overrides) is written for the team's own clients; applied to a calling
platform it would silently change that platform's prompts, so a gateway admin opts in per key with `ApplyTeamPolicy`.

### 5.4 Budgets, rate limits and rotation

Per plan answered question 3:

- `BudgetService.ApplicableBudgets(snapshot, view)` (`BudgetService.cs:56-67`) returns, unchanged in code,
  `KeyLineage(key.Id)` key budgets + `Team` budgets of the view's team + `Department` budgets of its department. The
  delegating key's **own team's and department's** budgets are not charged: that team is the platform's, not the
  requester's.
- Reservation and reconciliation stay one Lua script each (`BudgetService.cs:95-137`, `:166-204`); only the counter
  list differs. A 402 names the scope that ran out (`GatewayErrors.cs:140-158`), e.g. *"The team's budget … is used
  up"*, which the caller maps for its users.
- Rate limits are per key only today (`RedisKeys.RateRequests(keyId, …)`), so the key's limits cap the platform's
  total traffic; there is no per-team rate limit, and none is added (per-user limits stay in Ume.Rag, plan answered
  question 2).
- **Seeding agrees with the counters**: cold counters are summed from usage (`GatewayQueries.cs:59-91`) by
  `TeamId`/`DepartmentId` for team/department budgets and by `VirtualKeyId` over the rotation descendants for key
  budgets. Delegated records carry the effective team and department and the delegating key id (§5.5), so a seeded
  counter equals what the live counter would have been. Monthly totals group by key, team and department
  (`Migrations/20261012060353_DataRetention.cs:141-152`), so this also holds after retention.
- **Rotation (ADR 007)**: `KeyRotation.Rotate` (`src/Ume.LlmGateway.Domain/Services/KeyRotation.cs:64-87`) copies
  `DelegatedTeamIds`, `ApplyTeamPolicy` and `RequireMaxResidency`. Key budgets already follow the lineage (`GatewayCatalog.cs:110-111`); during a 24 h grace both
  keys delegate to the same teams and share the key budgets.

### 5.5 Usage records and reports

- `UsageRecord` (`Entities.cs:574-643`): `VirtualKeyId` = delegating key, `TeamId`/`DepartmentId` = effective team and
  its department (as today they come from `key.Team`, `GatewayRequestHandler.Accounting.cs:67-69`, now the view's),
  new `Delegated bool` (`NOT NULL DEFAULT false`, set from `key.OwnerTeam is not null`). No content, no end-user id.
  The delegating key's owner team is `VirtualKeys.TeamId` of `VirtualKeyId`, so it is not stored again.
- Migration `UsageDelegationAndRuns` adds `Delegated` and the two run columns of §7.1. Adding a column with a constant
  default is metadata-only in Postgres 11+, so the big table is not rewritten.
- `UsageRecordCopy.Columns` (`src/Ume.LlmGateway.Infrastructure/Persistence/UsageRecordCopy.cs:19-25`) and the COPY
  writes gain the three columns (the existing test that compares `Columns` with the EF model catches omissions).
- Admin reports already join the team by `u.TeamId` (`src/Ume.LlmGateway.AdminApi/ReportEndpoints.cs:195-200`,
  `:252-256`) and scope by `u.DepartmentId` (`AdminContext.cs:132-135`), so delegated use shows under the effective
  team and department, visible to that department's admins and viewers, with the platform key's name. *Usage →
  Requests* (`/api/usage/requests`, `ReportEndpoints.cs:55-70`) gains `delegated=true|false`; the existing `keyId`
  filter gives "everything this platform key did". The request list shows a *delegated* badge.
- Data API aggregate privacy rule: departments with fewer than `MinimumGroupSize` distinct keys in a period are folded
  (`DataEndpoints.cs:210-214`). A department whose only use is through the platform key counts as one key and is
  folded. Accepted: conservative, and the rule's purpose (no single-person view) still holds; documented.

### 5.6 Errors, response headers, security events

| Item | Value |
|---|---|
| Error | `403 delegation_not_allowed` (new `GatewayErrorCodes.DelegationNotAllowed`): *"The API key may not act for team '<value>'."* / *"… is not allowed to act for other teams."* / *"The team '<value>' or its department is deactivated."*; malformed header `400 invalid_request` |
| Response header | `x-ume-on-behalf-of: <team id>` (new `GatewayHeaders.OnBehalfOf`) |
| New event 1006 `gateway.delegation.refused` (Warning) | `RequestId`, `Reason` (`not_delegating`, `team_not_listed`, `team_inactive`), `OnBehalfOf` (the header value, `Printable`, ≤ 100), `Endpoint`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId` (the key's own); one per refused request, like 1003 |
| 1002 `gateway.pii.action`, 1003 `gateway.request.refused`, 1005 `gateway.content.flagged` | `TeamId`/`DepartmentId` are now the effective team's (they come from `key`, `GatewayRequestHandler.Accounting.cs:106-116`, `GatewayRequestHandler.Guard.cs:58-59`, `:111-112`); new attribute `Delegated` (attributes are only added, `SecurityEvents.cs:7-11`) |
| Data API security feed | `RefusalCodes` (`DataEndpoints.cs:62`) gains `delegation_not_allowed` |

### 5.7 Admin API, admin UI, audit

- **Setting the list: gateway admins only.** New `PUT /api/keys/{id}/delegation` `{ teamIds: Guid[], applyTeamPolicy:
  bool }` → `VirtualKey`, `RequireAuthorization(AdminPolicies.Admin)`; at most 1 000 ids, each an existing team (404
  otherwise), inactive teams allowed (they are refused per request until reactivated); 409 for a revoked key. Audit
  `Update VirtualKey` with `KeyAudit` (`AuditSnapshots.cs:75-84`) gaining `DelegatedTeamIds`, `ApplyTeamPolicy` and
  `RequireMaxResidency`; invalidation `Keys`. `KeyRequest` (`KeyEndpoints.cs:11-56`) does **not** carry the list or
  `ApplyTeamPolicy`, so the ordinary key form cannot change them.
- **Changing a delegating key: gateway admins only** (decision, open question 2). `PUT /api/keys/{id}`
  (`KeyEndpoints.cs:93-100`) answers 403 *"Only gateway admins can change a key that acts for other teams."* when the
  key has a non-empty list and the user is not a gateway admin: its models, residencies, PII and limits apply to every
  listed team, across departments, while key managers of the platform team are scoped to one department. Rotate and
  revoke stay with the key's managers (operations, never a widening).
- `KeyDto` (`Contracts.cs:29`) gains `delegatedTeamIds`, `applyTeamPolicy`, `requireMaxResidency`; the UI resolves
  names from the teams store.
- UI (`src/admin-ui/src/components/keys/KeyDetailDrawer.vue`, new `KeyDelegationDrawer.vue`): a *Acts for teams*
  section on the key detail (read-only for others), an *Edit teams* drawer for gateway admins with a filterable team
  checklist grouped by department (the rules page's owner picker, `composables/useRuleOwners.ts`, is the model); a
  badge *Delegating* in the key list; the key form shows a notice instead of the save button for non-admins.
- Docs: `docs/admin-api.md` *Virtual keys* (`:95-126`).

### 5.8 Team deactivation and deletion

- **Deactivated** team or department: the list is kept; requests for it get `403 delegation_not_allowed` reason
  `team_inactive`; reactivation restores delegation. Team and department updates already publish `Keys`
  (`OrganisationEndpoints.cs:65`, `:98`), so the cached views refresh at once.
- **Deleted** team: `DELETE /api/teams/{id}` (`OrganisationEndpoints.cs:101-110`) is refused with 409 *"Key '<name>'
  acts for this team; a gateway admin must remove the team from its delegation list first."* when a non-revoked key
  lists it, next to the existing "has keys" guard (`:104`). Unlike routing rules (ADR 014), a delegation is a grant held
  by a gateway admin, not something the team owns, so it is neither orphaned nor silently dropped; the caller (Ume.Rag)
  must also move its assistants first. Revoked keys keep stale ids (harmless, and the audit stays true).
- Departments cannot be deleted while they have teams (`:71`), so no department rule is needed.

### 5.9 Data API

| Endpoint | Change |
|---|---|
| `/v1/usage/records` (`DataEndpoints.cs:67-81`, `UsageRecordItem` `:10-16`) | `delegated`, `runId`, `step` appended (JSON fields added; CSV columns appended at the end) |
| `/v1/security/keys` (`:121-137`, `KeyInventoryItem` `:30-35`) | `delegatedTeamIds` |
| `/v1/catalog/keys` (`:146-148`) | `delegatedTeamIds` (ids only; lets Ume.Rag warn when an assistant's team is not on its key's list, with its `catalog.read`-only key) |
| `/v1/security/requests` | includes `delegation_not_allowed` refusals (§5.6) |

`deploy/postgres/app-roles.sql:50-53` and `deploy/kubernetes/base/app-roles.sql`: `"DelegatedTeamIds"` added to the
`ume_data` column grant on `VirtualKeys`. `UsageRecords` is granted whole-table (`:47`). `DatabaseRoleTests` updated.

### 5.10 Caching and invalidation

- `KeyAuthenticator.LoadAsync` (`KeyAuthenticator.cs:157-193`): when `DelegatedTeamIds` is non-empty, one more query
  loads those teams with their departments (`AsNoTracking`, `Include(t => t.Department)`), then
  `key.IndexDelegations(teams)` after `IndexAllowLists` (`:181`). Only on a cold or background load; the request path
  keeps zero Postgres calls (`docs/performance.md:9-11`).
- Invalidation: the delegation endpoint publishes `Keys`; team updates already publish `Keys`
  (`OrganisationEndpoints.cs:98`), department updates `Config` + `Keys` (`:65`); team creation needs nothing (a new
  team is on no list). The existing generation token (`KeyAuthenticator.cs:45-51`, `:195-200`) drops views with
  their key. TTL refresh (`Gateway:KeyCacheSeconds`, `:87-97`) reloads them too.
- `FindCached` for throttled addresses (`:76-77`) returns the same cached key with its views.

### 5.11 Endpoints

All endpoints that run step 1b honour the header: the JSON and multipart endpoints (`HandleAsync`) and live sessions
(`HandleRealtimeAsync`, budget at `GatewayRequestHandler.Realtime.cs:122`, slots per key id at `:89`). `GET /v1/models`
validates it (a misconfigured client learns at start-up) but lists the same models, because teams have no allow-lists.

### 5.12 Tests

| Test | Project |
|---|---|
| Views: same `Id`/allow-list indexes, `Team` swapped, `OwnerTeam` set; lookup by id (any case) and code; no allocation on lookup | `Domain.Tests/KeyTests.cs` |
| Rotation copies `DelegatedTeamIds` and both flags; key budget over the lineage charged for delegated use during grace | `Domain.Tests`, `Gateway.Tests/AccountingTests.cs` |
| Header from a non-delegating key, unknown team, unlisted team, inactive team, inactive department → 403 + 1006 with the right `Reason`; accounted against the owner team | `Gateway.Tests/DataPlaneTests.cs`, `SecurityEventTests.cs` |
| Delegated request: effective team's and department's budgets and the key's budget reserved and reconciled; the platform team's budgets untouched; 402 from the team budget | `Gateway.Tests/AccountingTests.cs`, `TokenQuotaTests.cs` (Redis store via Testcontainers) |
| Seeding: cold counters after delegated traffic equal the live counters | `Gateway.Tests` (Redis + Postgres) |
| Key rate limit caps delegated traffic for all teams together | `Gateway.Tests` |
| Team request policy (output cap, system prompt) **not** applied with `ApplyTeamPolicy = false`, applied when true; key's policy and model/residency/PII/attachment settings applied (intersection) | `Gateway.Tests` |
| Team-scoped routing rule of the effective team applies; one of the platform team does not | `Gateway.Tests/RoutingRuleGatewayTests.cs` |
| Usage record: key id, effective team/department, `Delegated`; COPY column list matches EF | `Gateway.Tests`, `IntegrationTests` |
| Invalidation: team deactivated → next request 403 without waiting for the TTL; team code change resolves the new code at once | `Gateway.Tests` |
| Admin: only gateway admins set the list; non-admin PUT on a delegating key 403; team delete 409; audit before/after | `AdminApi.Tests/AdminApiTests.cs` |
| Data API fields; `ume_data` can read `DelegatedTeamIds` and still not `KeyHash` | `DataApi.Tests`, `IntegrationTests/DatabaseRoleTests.cs` |
| UI: gateway admin edits the list; department admin sees it read-only and cannot save the key | e2e `journeys.spec.ts`, Vitest `KeyDetailDrawer.spec.ts`, `KeyFormDrawer.spec.ts`; `accessibility.spec.ts` |

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

### 7.1 Run grouping headers (feature A, release 2)

**Later (P2):** stored from release 2 on; Ume.Rag already sends them and a release-1 gateway ignores them.

- `x-ume-run-id` and `x-ume-step`: any key may send them. Trimmed; at most 100 characters kept (longer values are
  truncated, never refused: metadata must not fail a model call); control characters replaced
  (`SecurityEvents.Printable`, `SecurityEvents.cs:30-49`, which allocates nothing when there is nothing to replace).
- Stored on `UsageRecord` as `RunId varchar(100) NULL`, `Step varchar(100) NULL` (migration `UsageDelegationAndRuns`,
  §5.5). Not in monthly totals (retention summarises without them). No index: the admin filter `runId=` on
  `/api/usage/requests` runs inside the time range like the other filters.
- Exposed on `/v1/usage/records` (`runId`, `step`) and in the admin request list (column hidden by default).
- Documented as **opaque ids only**: no names, no content, no user ids (ADR 003). Ume.Rag sends GUIDs and step names
  such as `condense`, `answer`, `judge`.
- Shares migration `UsageDelegationAndRuns` with `Delegated`, so `UsageRecords` gets one migration and one COPY
  change.

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

### 7.3 `contextWindow` and `features` on `/v1/catalog/models` (optional, a later release)

Not needed for P1. The catalog model entry (`src/Ume.LlmGateway.DataApi/DataEndpoints.cs:152-154`) has no context size,
so the RAG admin cannot prefill an assistant's `QuerySteps.answer.contextTokens` (chat design §6.3) and editors type
it. Small item for any later release: add `ModelDeployment.ContextWindow` (already `int?` on the entity,
`src/Ume.LlmGateway.Domain/Entities/Entities.cs:309`) to the projection as `contextWindow` (null when unset), and
`ModelDeployment.Features` (`Entities.cs:310-311`) as `features` (tags incl. `tools`), so the RAG admin's
`GatewayModels` sync sets `SupportsTools` for agent assistants without a manual flag (tools design §6.4). Additive
fields, no migration.

Optional item next to these (admin design §4.10): `GET /v1/catalog/routes` (route alias name, kind, target model ids),
so the RAG admin syncs route aliases and their targets' residencies instead of platform admins keeping manual
`GatewayModels` rows for them. Additive route, `catalog.read` scope.

### 7.4 Rerank endpoint (P2, a separate release)

**Later (P2):** not in release 1 or 2 (§13.1). The gateway has no rerank endpoint today. Ume.Rag's `IReranker`
(`../knowledge-api/design.md` §6.7) needs one in P2 serving `bge-reranker-v2-m3` on-prem: query plus passages in, one
score per passage out. It goes through the same authentication, step 1b, ceiling, budget and usage path as the other
endpoints (presumably a new `GatewayEndpoint` value and model kind); `x-ume-max-residency` and
`RequireMaxResidency` apply, and Ume.Rag calls it with the `OnPrem` ceiling. Path, request shape and the serving
runtime are agreed with the gateway team before Ume.Rag's FakeGateway rerank fake (P1) is fixed (§16). Designed in
detail when P2 starts.

## 8. Data model, migrations, Redis

### 8.1 Migrations

| Feature | Migration | DDL | Compatible with the previous version because |
|---|---|---|---|
| C (release 2) | `DepartmentMemberGroups` | `Departments.MemberGroups varchar(256)[] NOT NULL DEFAULT '{}'` | additive with default; old code does not select it |
| B (release 1) | `KeyResidencyCeiling` | `VirtualKeys.RequireMaxResidency boolean NOT NULL DEFAULT false` | same; an old admin API updating a key leaves the column untouched |
| A (release 2) | `KeyDelegation` | `VirtualKeys.DelegatedTeamIds uuid[] NOT NULL DEFAULT '{}'`, `ApplyTeamPolicy boolean NOT NULL DEFAULT false` | same; an old admin API updating a key leaves the columns untouched |
| A (release 2) | `UsageDelegationAndRuns` | `UsageRecords.Delegated boolean NOT NULL DEFAULT false`, `RunId varchar(100) NULL`, `Step varchar(100) NULL` | old usage writer's COPY names its columns (`UsageRecordCopy.cs:27-28`), new columns take their defaults |

Grant changes only for A (§5.9). The migration service re-applies `app-roles.sql` on every start
(`deploy/postgres/app-roles.sql:1-5`).

### 8.2 Redis

No new keys: delegated budgets use the existing `ume:spend:*`/token counters of the effective team and department,
rate limits the existing per-key keys, and feature C reads only the token.

## 9. Settings

| Setting | Env | Feature | Default |
|---|---|---|---|
| `Oidc:TenantId` | `UME_OIDC_TENANT_ID` | C | – |
| `Oidc:RefuseGuests` | `UME_OIDC_REFUSE_GUESTS` | C | `true` |
| `DataApi:TenantId` | `UME_DATA_TENANT_ID` | C | `UME_OIDC_TENANT_ID` |
| `DataApi:RefuseGuests` | `UME_DATA_REFUSE_GUESTS` | C | `UME_OIDC_REFUSE_GUESTS` |

B and A need no settings: the ceiling is per request; delegation, `ApplyTeamPolicy` and `RequireMaxResidency` per key.
No `Gateway:*` setting is added; header length limits (100) and the delegation list limit (1 000) are constants,
documented. `Retry-After` reuses `CircuitBreaker:BreakDuration` (§3.3).

## 10. Security events

Ids follow `SecurityEvents.cs` (1xxx gateway, 2xxx admin API, 3xxx Data API; 1004 is unused and stays unused).
Names and attributes are only added (`docs/data-access.md:58-62`).

| Id | Name | Feature | When | Attributes |
|---|---|---|---|---|
| 1002, 1003, 1005 | existing | A | – | **+ `Delegated`**; `TeamId`/`DepartmentId` = effective team |
| 1006 | `gateway.delegation.refused` | A | §5.6 | `RequestId`, `Reason`, `OnBehalfOf`, `Endpoint`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId` |
| 2008 | `admin.signin.refused` | C | tenant or guest refusal | `Reason` (`wrong_tenant`, `guest_refused`), `Actor`, `SourceAddress` |
| 3003 | `data.token.refused` | C | Data API token refused by tenant/guest check, once a minute per (client, reason) | `Reason`, `Client`, `Count` |

SIEM alerts to add to `docs/security-events.md:28-34`: any 1006 with `team_not_listed` (a delegating key probing
teams, or a caller out of sync); any 2008 (a guest or foreign account was assigned to the application).

## 11. Performance and benchmarks

The gateway's rule: no Postgres on the request path with warm caches, 2 Redis round trips before the first byte, 1
after (`docs/performance.md:9-21`); allocation and call counts are the regression gates
(`docs/performance-improvement-plan.md:94-101`).

| Feature | Hot-path cost (warm caches) | Allocations | Round trips |
|---|---|---|---|
| B, header absent | 1 header lookup; 1 flag read (`RequireMaxResidency`) | 0 B | unchanged |
| B, header present | + span compare of ≤ 8 chars, bit-mask intersection, precomputed array | 0 B | unchanged |
| B, failure path | + re-check of the plan's targets (only when candidates come out empty) | small, failure path only | unchanged |
| A, header absent | 1 header lookup | 0 B | unchanged |
| A, header present | + `FrozenDictionary` lookup (code) or `Guid.TryParseExact` + lookup | 0 B (views built at cache load) | unchanged: same Lua scripts, 1–3 more counters in the budget script |
| A, response header | 1 header write (team id string cached on the view) | 0 B | – |
| Run headers | 2 header lookups; `Printable` | 0 B unless a value is over 100 characters or has control characters | – |
| Usage record | + 3 COPY values in the background writer | – | – |
| C | admin sign-in only (1 query) | – | none on the data plane |

Expected added time per request: well under 1 µs, against 196 µs in-memory / 1.65 ms Redis for a chat completion
(`docs/performance.md:237`). Cold key load for a delegating key: one more Postgres statement (the "key and
catalogue invalidated" audit scenario goes from 9 to 10 statements for such a key, by design).

Benchmarks:

| Benchmark | Expectation |
|---|---|
| `PipelineBenchmarks` (chat, stream, embeddings; `benchmarks/Ume.LlmGateway.Benchmarks/PipelineBenchmarks.cs`) | unchanged within noise; allocated bytes unchanged (no headers sent) |
| `PipelineBenchmarks` new case *Chat completion, delegated + ceiling + run id* | ≤ +1 % time, ≤ +200 B allocated vs. plain chat (the response header value and the larger usage record) |
| `RoutingBenchmarks.SelectCandidates` (`RoutingBenchmarks.cs:70`) + new case with ceiling | 0 B more than without |
| `audit` | every scenario: 0 Postgres on the request path, same Redis command count; new scenario *Delegated chat, warm caches* identical to *Chat, warm caches* |
| `benchmarks/compare` | not rerun for this release unless `PipelineBenchmarks` moves; the 2026-10-27 baseline stays the published one |

Compatible with improvement-plan item 2 (one Redis round trip, `docs/performance-improvement-plan.md:56-70`): the
effective team is known right after authentication, before the rate-limit call.

## 12. Gateway docs to update

| Page | Feature | Change |
|---|---|---|
| `docs/admin-api.md` (data plane `:360-415`, keys `:95-125`, organisation `:73-94`) | B, C, A | request headers `x-ume-max-residency`, `x-ume-on-behalf-of`, `x-ume-run-id`, `x-ume-step`; response headers incl. the missing `x-ume-residency` and the echo; codes `max_residency_not_met`, `delegation_not_allowed`, the 403/503 rule; `/v1/models` with a ceiling; `memberGroups`; `PUT /api/keys/{id}/delegation`; `delegatedTeamIds`, `applyTeamPolicy`, `requireMaxResidency`; `/bff/user` `departmentSources` |
| `docs/routing-rules.md` (`:181-206`) | B, A | ceiling as a hard constraint after rules (*Rules never widen access*); example rule on the header; team/department of a delegated request |
| `docs/providers-and-models.md` | B | residency section: what callers can enforce per request, `RequireMaxResidency`, what `x-ume-residency` reports |
| `docs/identity-and-roles.md` (`:26-35`, Entra `:160-186`, check `:217-229`) | C, A | department scope from member groups in the group claim (how to make Entra emit them); tenant and guest refusal; check-the-setup steps for `departmentSources`; delegating keys editable by gateway admins only; team request policy not applied to delegated requests |
| `docs/data-access.md` (events `:64-77`, permissions `:159-162`, aggregate rule `:237`) | C, A | events 1006, 2008, 3003, new attributes; new fields; delegated use and `MinimumGroupSize` |
| `docs/security-events.md` (`:20-46`) | C, A | SIEM alerts of §10; guests refused by default |
| `docs/runbook.md` (*Identity provider* `:68-121`, *Data API* `:342-372`) | C | member groups not applied (group claim missing or overage), tenant/guest settings |
| `docs/admin-ui.md` | C, A | Organisation member groups; key delegation section and flags; usage *delegated* filter and run id column |
| `docs/performance.md` | A | delegated key cold load (+1 statement); new benchmark case |
| `docs/upgrade-guide.md`, `CHANGELOG.md` | all | §13 |
| `docs/adr.md` | all | rows 018, 019, 020 (`adr-drafts.md`) |
| `docs/local-development.md` | development, C | FakeLlm dimensions; Keycloak `group-member` user |
| `README.md` header list, if it repeats the response headers | B | as admin-api |

## 13. Release plan

### 13.1 Contents

Two releases, so the pilot waits only for the smallest change a production gateway that other teams use must take
(reasoning: [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):

- **Release 1, in P1b before go-live** (built in a separate session in the gateway repo): §3 (feature B), §6 (FakeLlm `dimensions`), §7.2 (tool passthrough tests), ADR 018,
  the docs of §12 for B, and the upgrade-guide entry (§13.3).
- **Later (P2): release 2**, before the first second-department or group audience: §4 (feature C with the tenant/guest
  refusal), §5 (feature A), §7.1 (run grouping headers), ADRs 019–020, the remaining docs of §12, settings in compose,
  Portainer and Kubernetes examples, and the Keycloak realm.

The rerank endpoint (§7.4) is in neither: it is a separate P2 release.

### 13.2 Migrations and rolling update

**Migrations**: release 1 `KeyResidencyCeiling`; release 2 `DepartmentMemberGroups`, `KeyDelegation`,
`UsageDelegationAndRuns` and the `app-roles.sql` grant. **Rolling update: yes** for both, with these rules in the
upgrade guide:

1. Set `RequireMaxResidency` on a key only after every gateway instance runs release 1: an old instance ignores
   `x-ume-max-residency` (callers must check `x-ume-residency`). Release 2: set a delegation list or
   `ApplyTeamPolicy` only after every instance runs release 2: an old instance ignores `x-ume-on-behalf-of` (charges
   the key's own team, no echo header).
2. Do not rotate a key with `RequireMaxResidency` (release 1) or a delegating key (release 2) until the admin API is
   fully upgraded: an old instance's rotation does not copy the list or the flags.
3. Release 2: a usage-journal batch written by a new instance and replayed by an old one (only after a database
   outage during the rollout, `UsageWriter.cs:288`, `:373`) loses `delegated`, `runId` and `step` for those records.
4. Release 2: during the window an old admin API instance signs users in without group-derived scope and without the
   tenant/guest checks; sessions are fixed at sign-in, so affected admins sign in again after the rollout.

### 13.3 Upgrade-guide outline

**Release 1.** *Rolling update: yes* (rules above). New request header `x-ume-max-residency` and error code
`max_residency_not_met`; `503` with `Retry-After` for unavailable in-ceiling targets; `/v1/models` honours the
ceiling; `x-ume-residency` now documented; key flag `RequireMaxResidency`; FakeLlm takes `dimensions` /
`fake-embed-<n>` (development only). Nothing changes for clients that send no ceiling.

**Release 2.** *Rolling update: yes* (rules above). *Check before upgrading*: guests are now refused at sign-in by
default (`acct = 1` or a foreign `idp`): set `UME_OIDC_REFUSE_GUESTS=false` to keep admitting them; set
`UME_OIDC_TENANT_ID` on Entra. New request header `x-ume-on-behalf-of` and error code `delegation_not_allowed`;
delegating keys with `ApplyTeamPolicy`, editable by gateway admins only; team deletion refused while a key acts for the
team. *Opt in*: member groups on *Organisation* (the IdP must emit them in the group claim). New `/bff/user` field,
Data API `memberGroups`, team `code`, usage and key fields, events 1006/2008/3003 and attribute `Delegated`; run
headers (opaque ids only). Nothing changes for clients that send none of the new headers.

### 13.4 Ume.Rag afterwards

CI and tests run on the FakeGateway (`../platform/repo-and-deployment.md` §4.3), which implements the release-1
contract (and the release-2 echo).

**P1a, before release 1** (Decisions "P1a plan answers", "Interim gateway for P1a"): Ume.Rag calls the gateway as it
is (v0.8.5), on an interim **test/dev gateway instance** on a fixed host (HTTPS, public certificate) with EU-hosted
embedding and chat models; it is the interim J10, and the same host's Keycloak holds the shared realm `ume` (J11).
Address and keys live only in user secrets or `deploy/.env` and secret files, never in the repository. Everything is
EU-hosted until on-prem hardware exists (register M2): the pilot embedding profile is `text-embedding-3-large` at 1024
dimensions, requested with `dimensions`. Ume.Rag already behaves as for release 1: every call carries
`x-ume-max-residency` with the level's key and the answer's `x-ume-residency` is checked before a byte is used
(`ResidencyCeilingHandler`); v0.8.5 ignores the request header, so until release 1 the ceiling rests on the keys'
`AllowedResidencies` and that check. `GET /v1/models` is not yet filtered by the ceiling.

**With release 1, in P1b before go-live:**

- **Keys per level of the pilot department's team** (plan §4): ordinary team keys `OnPrem`-only (`AllowedResidencies = [OnPrem]`), `Eu`-max (`[OnPrem, Eu]`) and open
  (all), each issued per deployable that needs it with `AllowedIpRanges` of that deployable's hosts (`Entities.cs:89`)
  and `RequireMaxResidency = true`. The worker never holds the open key. Which deployable holds which level is the
  platform plan's (§6). Every Ume.Rag call is charged to the pilot team. The team's admins can edit these keys (no
  delegation, so not gateway-admin only); the explicit ceiling and the `x-ume-residency` check still hold whatever
  they change. The gateway applies the team's request policy to non-delegated requests, so the pilot team keeps none.
  The worker's keys also carry the gateway's existing `MaxConcurrentRequests` cap (429 + `Retry-After`), so a
  misconfigured worker cannot starve chat (processing design §11).
- Every call carries `x-ume-max-residency` (`OnPrem`, `Eu` or `External` for `public`) and the run headers (stored
  from release 2 on; release 1 ignores them); no `x-ume-on-behalf-of`. The answer's `x-ume-residency` is verified.
- `OrgSync` with a `catalog.read` Data API key reads departments and teams (no `memberGroups` or team `code` yet).
- Saving an assistant calls `GET /v1/models` with the level's key and `x-ume-max-residency` = the level's ceiling,
  and refuses a model that is not listed (§3.4, admin design §4.6 V8).

**Later (P2), with release 2:**

- **Delegating keys per level** replace the pilot team's keys: same levels, deployables and `AllowedIpRanges`,
  `RequireMaxResidency = true`, `ApplyTeamPolicy = false`, owned by the platform team, list = teams with assistants;
  the pilot team's keys are revoked.
- Every call also carries `x-ume-on-behalf-of: <team id>`; the echo is verified (a missing echo fails closed under
  `Gateway:RequireDelegationEcho`, identity design §4.7).
- `OrgSync` reads `memberGroups` and team codes; the same `TenantId`/`RefuseGuests` as the gateway.
- `GET /v1/models` on assistant save is sent on behalf of the assistant's team.

## 14. Changes to other documents in this repo

What this design needs from the other documents of this repo, and they state:

1. **`../identity/*`**: two releases, claims-only feature C, keys per level, explicit ceiling incl. `External`,
   `Gateway:RequireDelegationEcho` (from release 2).
2. **`../chat-api/plan.md`, `../chat-api/design.md`**: no attribution modes or linked keys; `External` for `public`;
   a `503` with `Retry-After` from the gateway is retryable, `403 max_residency_not_met` is not.
3. **`../platform/plan.md`** §6–§7: keys per level and deployable (the pilot team's until release 2, delegating
   after); no linked keys; two gateway releases.
4. **`../platform/data-model.md`** §2.1: no `GatewayKeyLinks`; no GIN index on `Departments.MemberGroups`;
   `Teams.Code` from `/v1/catalog/teams` `code` (release 2).
5. **`../platform/repo-and-deployment.md`** §4.3 FakeGateway: `RequireMaxResidency` behaviour (400 without the
   header), the 403/503 rule with `Retry-After`; §4.4 FakeLlm `dimensions` with release 1.
6. **`../knowledge-api/design.md`**, **`../ingestion-worker/design-engine.md`**: delegation after release 2; worker
   keys never include the open key.
7. **`../../01-general-idea.md`** §10 Decisions rows "Gateway changes" and "Weakness review 5": features, release
   split, key flags, 503 rule.
8. **`../to-verify.md`**: rows 82 and 86 (Graph) in P2; the rows of §16.
9. **`../platform/repo-and-deployment.md`** §4.3 FakeGateway: `/v1/models` filtered by `x-ume-max-residency`; a rerank
   fake on the path agreed for §7.4 (the FakeGateway serves `/v1/rerank` until then).

## 15. Answered questions (2026-10-29)

1. **Guests refused by default for every adopter**: yes, `Oidc:RefuseGuests` and `DataApi:RefuseGuests` default to
   `true`, with a prominent *Check before upgrading* in the upgrade guide (§13.3).
2. **Delegating keys editable by gateway admins only**: yes, every change except rotate and revoke (§5.7).

## 16. To verify

- `TokenValidatedContext.Properties.Items` set in `OnTokenValidated` reach the cookie ticket and
  `CookieSigningInContext.Properties` (expected from the OIDC handler's sign-in, not checked in code).
- `ctx.Fail(...)` in `OnTokenValidated` + `OnRemoteFailure` with `HandleResponse()` gives a clean redirect on .NET 10.
- EF Core maps the new non-mapped `VirtualKey` members only if ignored explicitly; whether `GatewayDbContext`
  (`:62-70`) needs `e.Ignore(...)` for `OwnerTeam`.
- Entra `acct` and `idp` values for B2B guests and members in ID tokens and in delegated access tokens for the Data
  API; whether a member's ID token ever carries `idp` equal to the v1 issuer form.
- Docker Compose nested default `${A:-${B:-true}}` (one level is already used at `deploy/compose.prod.yaml:181`).
- The OpenAI .NET SDK's default `encoding_format` for embeddings (base64 or float).
- Ollama's OpenAI-compatible `/v1/embeddings` and a `dimensions` parameter (only matters if a caller sends it to real
  BGE-M3; Ume.Rag does not).
- That no gateway code path reads `key.TeamId` meaning "the key's owner" after step 1b besides those listed in §5.1
  (grep at implementation time; `AuthFailureRecorder.cs:57` runs before step 1b and keeps the owner).
- Realtime: whether WebSocket clients used by adopters can set custom headers on the upgrade request (server SDKs can;
  browsers cannot, which only matters for features B and A, not for Ume.Rag).
- The attempt loop can tell that every attempted candidate was circuit-open or cooling down at selection
  (the selection's `CircuitStatus`, `Stores.cs:175`, is available after `SelectCandidatesAsync`), for the 503 of §3.3.
- Entra emits member groups in the admin registration's group claim with the chosen `groupMembershipClaims`
  setting (identity design §2.5), and how often pilot admins hit the overage limit.
- (P1, before the FakeGateway fake is fixed): the P2 rerank endpoint's path and request shape, and which
  on-prem runtime serves `bge-reranker-v2-m3` behind it (§7.4).

## 17. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md) §6–§7:

| Removed or deferred | Where it went |
|---|---|
| Releases B → C → A (three upgrade windows, per-release upgrade guides, ADR rows per release) | two releases: B in release 1, C and A in release 2 (§13, §18) |
| Graph `checkMemberGroups` lookup (`Oidc:GroupLookup`, `GraphTokenClient`, `IGroupMembershipSource`), `GroupMember.Read.All`, Redis `ume:groups:{oid}`, circuit, event 2007, `departmentLookup` on `/bff/user`, attribute `DepartmentLookup` on 2002, the "failed" banner, directory group search endpoints | P2, with data-plane audiences and approval package 2 (identity plan §2); P1 member groups are entered as group claim values |
| Effective team's request policy on delegated requests | removed; key's policy only, opt-in `ApplyTeamPolicy` (§5.3) |
| `403 max_residency_not_met` while in-ceiling targets are only drained | `503 no_eligible_provider` + `Retry-After` (§3.3) |
| Ume.Rag interim: per-team linked keys `GatewayKeyLinks`, `onprem` keys, the "platform service key" (one key) | removed; FakeGateway in CI and tests, the interim test/dev gateway in P1a, the pilot team's keys per level and deployable until release 2, then delegating keys (§13.4) |

## 18. Deferred or removed (weakness review)

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Removed or deferred | Where it went |
|---|---|
| One combined release before the pilot (B, C, A; ADRs 018–020 together) | release 1 (B, FakeLlm `dimensions`, tool passthrough tests, ADR 018) before the pilot; release 2 (C with tenant/guest refusal, A, run headers, ADRs 019–020) in P2 (§13) |
| `RequireMaxResidency` in migration `KeyDelegation` | its own migration `KeyResidencyCeiling` in release 1 (§8.1) |
| Ume.Rag on delegating keys from the pilot on | the pilot team's per-level keys until release 2 (§13.4) |
