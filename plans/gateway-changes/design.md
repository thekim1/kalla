# Gateway changes: detailed design

Detail step 4 of the Ume.Rag design. Details [`plan.md`](plan.md) (binding, incl. its answered questions 1–6) and the
gateway side of [`../identity/design.md`](../identity/design.md) §1.4, §1.5, §1.7, §2 and §10 (feature C and the
tenant/guest refusal are designed there for both systems; this document only turns them into gateway work). The ADR
rows are in [`adr-drafts.md`](adr-drafts.md). Nothing here is implemented; nothing in `~/git/llmgateway` was changed.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5). "Pipeline" means
`src/Ume.LlmGateway.Gateway/Pipeline/`; "Entities.cs" means `src/Ume.LlmGateway.Domain/Entities/Entities.cs`.

## 1. Overview

| Release | Content | Migration | Rolling update | RAG platform afterwards |
|---|---|---|---|---|
| **B** (provisionally 0.9.0) | Residency ceiling `x-ume-max-residency` (§3); FakeLlm embedding dimensions (§6); tool passthrough tests (§7.2) | none | yes | sends the ceiling; drops the separate `onprem` linked keys; local dev against the real gateway with FakeLlm |
| **C** (0.9.1) | Department member groups, Graph lookup, `/bff/user` additions (§4); tenant and guest refusal for admin sign-in and Data API tokens (§4.6); `code` on catalog teams | `DepartmentMemberGroups` | yes | syncs `memberGroups` and team codes; same group rule as the gateway; same `TenantId`/`RefuseGuests` |
| **A** (0.9.2) | Delegated attribution `x-ume-on-behalf-of` (§5); run grouping headers (§7.1) | `KeyDelegation`, `UsageDelegationAndRuns` | yes | one platform service key; `GatewayKeyLinks` retired |

Order B → C → A is decided (plan answered question 5). Version numbers are provisional; the gateway's own release
practice decides them.

## 2. Where the changes sit in the request pipeline

Current order in `GatewayRequestHandler.HandleAsync` (`GatewayRequestHandler.cs:56-244`), with the new steps:

| # | Step (current) | Citation | New |
|---|---|---|---|
| 1 | Authenticate key; status, owner active, address list | `GatewayRequestHandler.cs:64-71`, `GatewayRequestHandler.Stages.cs:21-77` | – |
| **1b** | – | – | **Read request headers** (§2.1): ceiling (B), delegation (A), run grouping (A). A malformed value or refused delegation is answered here, before the body is read |
| 2 | Parse body, model name | `GatewayRequestHandler.cs:73-96` | – |
| 2b–c | Attachment policy, provider tools/storage (key) | `:98-115` | applies the delegating key's settings (A) |
| 3 | Resolve model; key model/provider allow-lists; endpoint | `:117-128`, `RouteResolver.cs:58-90` | – (the ceiling is not checked here: a routing rule may still send the request on-prem) |
| 4 | Rate limit (key) | `:130-140`, `GatewayRequestHandler.Stages.cs:83-116` | key's limit also under delegation (A) |
| 5 | PII guard → `restrictTo = OnPrem` | `:142-173` | – |
| 5b | Content guard (key) | `:175-180` | – |
| 6 | Routing rules (scoped by key lineage, team, department) | `:182-189`, `GatewayRequestHandler.Stages.cs:122-139`, `:361-415` | team and department = effective team's (A); rules can read the new headers like any header |
| 7 | Plan + candidate selection: provider list, residency, PII restriction, endpoint, circuit order, streaming, context window | `:191-201`, `GatewayRequestHandler.Stages.cs:169-205`, `RouteResolver.cs:143-188` | **ceiling intersected with the key's residencies and `restrictTo`** (B) |
| 7b | Request policy (team + key) | `:203-213` | effective team's policy (A) |
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

| Header | Release | Value | Malformed → |
|---|---|---|---|
| `x-ume-max-residency` | B | `OnPrem`, `Eu` or `External`, case-insensitive (§3.1) | `400 invalid_request` |
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
  i.e. `OnPrem`, `Eu`, `External`. Callers compare case-insensitively (see §14 for the RAG documents that wrote `EU`).

**Reconciliation with the RAG documents.** Identity design §4.7, chat-api §4 and platform plan §6 write `EU` and
`OnPrem`; both parse. The canonical spelling in the gateway docs is `Eu`, and the RAG documents switch to it (§14) so a
string comparison against `x-ume-residency` (which says `Eu`) does not fail. The handover's `Any` is dropped.

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
| Malformed header | `400 invalid_request` | as §3.1 |
| Candidates empty **and** the ceiling removed at least one target that would otherwise be eligible | `403 max_residency_not_met` (new code in `GatewayErrorCodes`, `GatewayErrors.cs:10-40`) | *"No provider for '<model>' is available at or below the residency 'OnPrem' that the request allows."* |
| Candidates empty for other reasons (PII on-prem restriction, key lists, all drained) | unchanged `503 no_eligible_provider` (`GatewayErrors.cs:117-119`) | unchanged; the PII text wins when `restrictTo` is set, since `OnPrem` is inside every ceiling |

The "removed by the ceiling" test runs only on the failure path: `SelectCandidatesAsync` re-checks eligibility of the
plan's targets without the ceiling when the list comes out empty. 403 matches the existing "key may not use any
provider that offers …" (`GatewayErrors.cs:111-112`) and the RAG FakeGateway (`../platform/repo-and-deployment.md`
§4.3). A temporarily drained on-prem provider therefore also gives 403 while an EU provider is up; accepted (the caller
must not retry wider, and the message says why).

Error bodies use the existing shapes (`GatewayErrors.cs:210-235`): OpenAI style, Anthropic style on `/v1/messages`,
Realtime `error` events on live sessions. The refusal is accounted like every refusal (`RejectAsync`,
`GatewayRequestHandler.Stages.cs:294-298`), with `ErrorCode = max_residency_not_met`. It is **not** a security event:
the client restricted itself.

Response headers: none new. `x-ume-residency` (`GatewayHeaders.cs:15`, set in `SetRoutingHeaders`,
`GatewayRequestHandler.Stages.cs:338-344`) reports the answering provider and is documented in `docs/admin-api.md`,
whose header list (`docs/admin-api.md:382`) misses it today. A caller that must be sure checks it before using the
answer: during a rolling update to B an older instance ignores the request header.

### 3.4 Interactions

| With | Behaviour |
|---|---|
| Routing rules | Rules run first and may choose an on-prem model for a ceiling request; conditions see the header like any header (`GatewayRequestHandler.Stages.cs:363-371`), e.g. `headers["x-ume-max-residency"] == "OnPrem"` (value as the client sent it). A matched rule whose targets are all above the ceiling ends in `max_residency_not_met`: no return to the requested model (ADR 013). |
| PII on-prem restriction | Intersected; `OnPrem` is within every ceiling, so the PII restriction alone decides when both are set. |
| Key residency list | Intersected. A key limited to `Eu` with ceiling `OnPrem` has no eligible residency → `max_residency_not_met`. |
| Fallback, retry, circuits | Only filtered candidates are attempted; open circuits still go last (`RouteResolver.cs:176-180`) but never return a removed target. |
| Streaming | The streaming filter (`RouteResolver.cs:181`) runs after the ceiling; the decision is made before any byte is sent, so a streamed answer never starts above the ceiling. |
| Token quotas per residency | Computed from the filtered candidates (`GatewayRequestHandler.cs:224-226`), so quotas follow automatically. |
| Session affinity | Unchanged; rendezvous runs over the filtered pool. |
| Context-window filter | Runs after selection (`GatewayRequestHandler.Stages.cs:187-199`). |

| Endpoint | Applies | Note |
|---|---|---|
| `/v1/chat/completions`, `/v1/responses`, `/v1/messages`, `/v1/messages/count_tokens` | yes | translated paths (Anthropic, Responses bridge) are chosen per target after selection |
| `/v1/embeddings` | yes | RAG embeddings are always on-prem anyway |
| `/v1/audio/transcriptions`, `/v1/audio/translations`, `/v1/images/generations`, `/v1/images/edits` | yes | same `SelectRouteAsync` path |
| `/v1/realtime`, `/v1/realtime/translations` | yes | read from the WebSocket upgrade request; passed at `GatewayRequestHandler.Realtime.cs:82`; the client-chosen input transcription model is checked with the same set (`:215`) |
| `GET /v1/models` | yes | lists only models with a target within the ceiling, and `ume.residencies` shows only those (`GatewayRequestHandler.Models.cs:22`, `:44`), so a caller can ask "what may I use at `OnPrem`" |

### 3.5 Code changes

| File | Change |
|---|---|
| `src/Ume.LlmGateway.Domain/Services/ResidencyCeiling.cs` (new) | `TryParse`; `Mask(DataResidency)`; eight precomputed `DataResidency[]` (one per subset) so intersections never allocate |
| `src/Ume.LlmGateway.Domain/Services/RouteSelector.cs:213-223` | overload with `ceiling`; bit-mask intersection, returns a precomputed array or null |
| `src/Ume.LlmGateway.Domain/Entities/Entities.cs:158-166` | `IndexAllowLists` also stores `AllowedResidencyMask` (private field like the model index) |
| `Pipeline/GatewayRequestHeaders.cs` (new) | §2.1 |
| `Pipeline/RouteResolver.cs:49-50`, `:143-188` | `ceiling` parameter; `max_residency_not_met` on the failure path |
| `Pipeline/GatewayRequestHandler.cs`, `.Stages.cs`, `.Realtime.cs`, `.Models.cs` | step 1b; pass the ceiling |
| `Pipeline/GatewayErrors.cs` | `GatewayErrorCodes.MaxResidencyNotMet`, `GatewayError.InvalidHeader(name, expected)`, `GatewayError.MaxResidencyNotMet(model, ceiling)` |
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
| PII reroute + ceiling `Eu` → on-prem; PII reroute with no on-prem target → 503 with the PII message | `Gateway.Tests` |
| Streaming: 403 before any SSE byte; `x-ume-residency` on success | `Gateway.Tests` |
| Every endpoint in the table of §3.4 honours it (theory over `GatewayEndpoints.All`) incl. `/v1/models` listing and realtime upgrade | `Gateway.Tests`, `RealtimeTests.cs` |
| Malformed header → 400 in OpenAI and Anthropic shapes; accounted with `invalid_request` | `Gateway.Tests` |
| Token quota limited to `External` not reserved when the ceiling is `Eu` | `Gateway.Tests/TokenQuotaTests.cs` |

## 4. Feature C: department scope from member groups, and tenant/guest refusal

Designed in `../identity/design.md` §2 (both systems) and §1.7; this section is the gateway's file-level plan for the
work items of §2.7 there. Deviations are listed in §4.10.

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
| `GET /api/directory/groups?search=` and `GET /api/directory/groups/{id}` (new, gateway-admin) | Graph group search and display name, only with `GroupLookup = Graph` (else 404); never stored; `admin.data.read` not needed (no personal data) |
| `src/admin-ui/src/components/org/DepartmentFormDrawer.vue` | *Member groups* field: chips; with Graph a search picker showing display names, else free text with the hint "Values of the group claim: Entra object ids, AD FS or Keycloak group names" |
| `DepartmentDetail.vue` | lists member groups (display names when Graph resolves them) |
| `src/admin-ui/src/stores/auth.ts` and layout | banner *"Departments from groups could not be looked up. You may see fewer departments than usual."* when `departmentLookup = "failed"` |

### 4.3 Sign-in resolution

New in `src/Ume.LlmGateway.AdminApi/Identity/` (same shape as the RAG copy, identity design §2.7 last paragraph):

- `DepartmentMembershipResolver.ResolveAsync(ClaimsPrincipal, IReadOnlyList<DepartmentGroups>, OidcOptions,
  IGroupMembershipSource, CancellationToken) → MembershipResult(Codes, Sources, Lookup)` implementing steps 1–5 of
  identity design §2.3. `DepartmentGroups` = (cost-centre code, member groups) of **active** departments with at least
  one member group, loaded in one query per sign-in (sign-ins are rare; no cache).
- `IGroupMembershipSource` with `GraphGroupMembershipSource` (§4.4) and `ClaimsOnlyMembershipSource` (no-op).
- Called from `OnTokenValidated` (`src/Ume.LlmGateway.AdminApi/AdminAuthentication.cs:123-127`) right after
  `ApplyClaimMapping`, after the tenant/guest check (§4.6). The handler becomes async.
- Group codes are added as ordinary `departmentCodes` claims, so `AdminContext.Departments()`
  (`src/Ume.LlmGateway.AdminApi/AdminContext.cs:81-90`) and every scoped query are unchanged. Roles are never added.
- The result is stored in the ticket's `AuthenticationProperties.Items` (`.ume.departmentLookup`,
  `.ume.departmentSources` as compact JSON): `TokenValidatedContext.Properties` flows into the cookie sign-in, the
  ticket lives in Redis (`AdminSessions.cs`), and *Stay signed in* re-signs with the same properties
  (`AdminAuthentication.cs:217-228`), so nothing is recomputed on extension. Scope stays fixed at sign-in, as today.

### 4.4 Graph lookup (Entra only)

- `GraphGroupMembershipSource` asks `POST https://graph.microsoft.com/v1.0/users/{oid}/checkMemberGroups` for the
  GUID-shaped candidates not in the token and not in the cache's `checked` set, 20 ids per call, 3 s timeout, one retry,
  circuit (`AddStandardResilienceHandler` from ServiceDefaults). Non-GUID values are never sent.
- **Token without MSAL**: a small `GraphTokenClient` posts `client_credentials` to
  `https://login.microsoftonline.com/{Oidc:TenantId}/oauth2/v2.0/token` with `Oidc:ClientId`/`Oidc:ClientSecret`,
  scope `https://graph.microsoft.com/.default`, and caches the token until 5 minutes before expiry. The gateway avoids
  dependencies where ~40 lines do (cf. ADR 013); the RAG copy may use MSAL (§4.10).
- Cache: Redis `ume:groups:{oid}` (new `RedisKeys.GroupMembership(string oid)` in
  `src/Ume.LlmGateway.Infrastructure/Stores/RedisKeys.cs`), 10 minutes, value `{ "checked": [ids], "member": [ids] }`,
  behind a new `IGroupMembershipCache` with Redis and in-memory implementations like the other stores. The existing
  ACL (`~ume:*`, `+get +set +setex`, `deploy/redis/users.acl.example`) covers it. Not cached after a failure.
- Failure (error, timeout, open circuit): sign-in continues with claim codes and claim-visible groups only,
  `departmentLookup = "failed"`, event 2007 `admin.group_lookup.failed`. Fail closed with respect to scope (identity
  design §2.6).
- Startup validation (`OidcOptions` with `ValidateOnStart`): `GroupLookup = Graph` requires `TenantId` and
  `ClientSecret`; refused otherwise with a clear message.
- Needs application permission `GroupMember.Read.All` on the admin registration (Ume.Rag's Bicep package 1 owns the
  shared registration, identity design §12) and outbound HTTPS to `graph.microsoft.com` and
  `login.microsoftonline.com` from the admin API (it already reaches the IdP and `models.dev`).

### 4.5 `/bff/user` and events

- `/bff/user` (`AdminAuthentication.cs:188-208`) adds `departmentSources: [{ code, source: "claim" | "group" }]` and
  `departmentLookup: "claims" | "graph" | "failed"`, read from the ticket properties. No group ids.
- `admin.signin` (2002, `src/Ume.LlmGateway.Infrastructure/Security/SecurityEvents.cs:96-98`, emitted in
  `OnSigningIn`, `AdminAuthentication.cs:95-110`) gains the attribute `DepartmentLookup`, read from
  `CookieSigningInContext.Properties`.
- New 2007 `admin.group_lookup.failed` (Warning): `Actor` (sub), `Operation` (`checkMemberGroups`, `token`), `Status`
  (HTTP status or `timeout`, `circuit_open`).

### 4.6 Tenant and guest refusal (admin sign-in and Data API tokens)

**Admin sign-in.** In `OnTokenValidated`, before claim mapping:

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
  (data-model §2.1) that the catalog does not deliver today. Not needed for delegation any more (§5.2), but cheap and
  useful for display; shipped with C because RAG's `OrgSync` starts then.
- `docs/data-access.md:162` (catalog row) updated.

### 4.8 Settings

| Env (`deploy/.env`) | Setting | Default | Service |
|---|---|---|---|
| `UME_OIDC_GROUP_LOOKUP` | `Oidc:GroupLookup` | `Claims` | admin API; `Claims` or `Graph` |
| `UME_OIDC_TENANT_ID` | `Oidc:TenantId` | – | admin API; required for `Graph` |
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
| Identity design §2.8 unit cases (merge without duplicates, case-insensitive, inactive departments ignored, never a role, overage, only unchecked GUID candidates, ≤ 20 per call, failure → claims only + 2007, nothing cached) | `AdminApi.Tests/DepartmentMembershipTests.cs` (new), fake `IGroupMembershipSource`, `TestClock` |
| Tenant/guest: `tid` mismatch, missing `tid` with `TenantId` set, `acct=1`, foreign `idp`, member `idp` equal to issuer passes, Keycloak token unaffected; redirect, no session, event 2008 | `AdminApi.Tests/OidcClaimMappingTests.cs` |
| `/bff/user` fields; 2002 carries `DepartmentLookup`; extension keeps the properties | `AdminApi.Tests/AdminSessionTests.cs`, `SecurityEventTests.cs` |
| Member groups CRUD, validation limits, gateway-admin only for edit, department admin reads, audit before/after | `AdminApi.Tests/AdminApiTests.cs` |
| Data API: `memberGroups`, team `code`; token with wrong `tid` or guest claims → 401 + 3003; keys unaffected | `DataApi.Tests/DataApiTests.cs` |
| Migration adds the column with default on a populated database | `IntegrationTests/MigrationServiceTests.cs` |
| Keycloak user `group-member` (no department attribute, group `IFO-medarbetare` = member group of 3000 *Individ- och familjeomsorgen*, `MigrationService/DevSeeder.cs:73`) sees department 3000 and `departmentSources` `group` | e2e Playwright (`tests/e2e/specs/journeys.spec.ts`), realm `dev/keycloak/ume-realm.json` gains the group, the user and a *Group Membership* mapper (claim `groups`, full path off) |
| Organisation drawer: member groups field, accessibility | e2e `accessibility.spec.ts`, `OrganisationView.spec.ts` (Vitest) |

### 4.10 Deviations from the identity design (flagged)

1. **GIN index dropped** (identity §2.7 item 1): the gateway reads all active departments' groups at sign-in and never
   queries by group, so the index would only cost writes. The RAG side keeps whatever its queries need.
2. **No MSAL in the gateway** (identity §2.4 says MSAL for the RAG copy): the token client is ~40 lines; the classes
   stay copyable because the token source is behind `IGroupMembershipSource`.
3. **Refusal is a redirect, not a bare 403** (identity §1.7 "ends the sign-in with 403"): in a browser OIDC callback
   a 403 body cannot be shown by the SPA. The RAG BFF should do the same (§14).
4. **`GroupLookup = Graph` requires `Oidc:TenantId`** (implied but not stated in identity §1.4).
5. **Gateway event ids**: the RAG platform logs its refusal as 5001 with a `Reason`; the gateway has no equivalent
   sign-in failure event, so it gets 2008 `admin.signin.refused` and 3003 `data.token.refused`.

## 5. Feature A: delegated attribution

### 5.1 Model

- `VirtualKey` (`Entities.cs:46-116`) gains `public List<Guid> DelegatedTeamIds { get; set; } = [];` (empty = not
  delegating). An array, like `AllowedModels`/`AllowedResidencies` (`:76-82`) and `RoutingRule.ScopeIds`: no join
  table, so the key loads, rotates and audits as one row.
- Migration `KeyDelegation`: `ALTER TABLE "VirtualKeys" ADD "DelegatedTeamIds" uuid[] NOT NULL DEFAULT '{}'`. No index
  (the only query by team is the rare delete guard, §5.8, over a small table).
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
  later refusals). A caller can verify that the gateway applied the delegation: an instance that predates release A
  ignores the request header and sends no echo (§8.3).

### 5.3 What applies to a delegated request

| Aspect | Source | Citation (current code that reads it) |
|---|---|---|
| Model allow-list, provider allow-list, residency allow-list | delegating key (teams have none, so the intersection is the key's lists) | `RouteResolver.cs:73-82`, `:149-155` |
| Residency ceiling (B) | request header, intersected | §3.2 |
| PII policy, attachment policy, provider tools/storage, content guard | delegating key | `GatewayRequestHandler.cs:98-180` |
| Rate limits, tokens per minute, concurrency, live-session slots | delegating key (overall cap) | `GatewayRequestHandler.Stages.cs:83-116`, `GatewayRequestHandler.Realtime.cs:89` |
| Request policy | `Merge(effective team, key)` – the effective team's policy with the key's on top, as for any key; the platform team's own policy does not apply | `GatewayRequestHandler.cs:205`, `src/Ume.LlmGateway.Domain/Services/RequestPolicy.cs:46-65` |
| Routing rules | key-scoped rules of the delegating key's lineage, team and department rules of the **effective** team, global rules; `team_*`/`department_*` variables are the effective team's | `GatewayRequestHandler.Stages.cs:397-414` |
| Budgets and token quotas | delegating key's lineage + effective team + effective department (§5.4) | `BudgetService.cs:56-67` |
| Session affinity | scoped to the key id (unchanged) | `GatewayRequestHandler.Stages.cs:155` |

Delegation can never widen access: every allow-list is the key's; teams carry no allow-lists of their own
(`Entities.cs:21-40`); rules never widen (ADR 013). What changes is *whose* money, policy and rules apply.

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
  `DelegatedTeamIds`. Key budgets already follow the lineage (`GatewayCatalog.cs:110-111`); during a 24 h grace both
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

- **Setting the list: gateway admins only.** New `PUT /api/keys/{id}/delegation` `{ teamIds: Guid[] }` →
  `VirtualKey`, `RequireAuthorization(AdminPolicies.Admin)`; at most 1 000 ids, each an existing team (404 otherwise),
  inactive teams allowed (they are refused per request until reactivated); 409 for a revoked key. Audit `Update
  VirtualKey` with `KeyAudit` (`AuditSnapshots.cs:75-84`) gaining `DelegatedTeamIds`; invalidation `Keys`.
  `KeyRequest` (`KeyEndpoints.cs:11-56`) does **not** carry the list, so the ordinary key form cannot change it.
- **Changing a delegating key: gateway admins only** (decision, open question 2). `PUT /api/keys/{id}`
  (`KeyEndpoints.cs:93-100`) answers 403 *"Only gateway admins can change a key that acts for other teams."* when the
  key has a non-empty list and the user is not a gateway admin: its models, residencies, PII and limits apply to every
  listed team, across departments, while key managers of the platform team are scoped to one department. Rotate and
  revoke stay with the key's managers (operations, never a widening).
- `KeyDto` (`Contracts.cs:29`) gains `delegatedTeamIds`; the UI resolves names from the teams store.
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
| Rotation copies `DelegatedTeamIds`; key budget over the lineage charged for delegated use during grace | `Domain.Tests`, `Gateway.Tests/AccountingTests.cs` |
| Header from a non-delegating key, unknown team, unlisted team, inactive team, inactive department → 403 + 1006 with the right `Reason`; accounted against the owner team | `Gateway.Tests/DataPlaneTests.cs`, `SecurityEventTests.cs` |
| Delegated request: effective team's and department's budgets and the key's budget reserved and reconciled; the platform team's budgets untouched; 402 from the team budget | `Gateway.Tests/AccountingTests.cs`, `TokenQuotaTests.cs` (Redis store via Testcontainers) |
| Seeding: cold counters after delegated traffic equal the live counters | `Gateway.Tests` (Redis + Postgres) |
| Key rate limit caps delegated traffic for all teams together | `Gateway.Tests` |
| Team request policy (output cap, system prompt) applied; key's model/residency/PII/attachment settings applied (intersection) | `Gateway.Tests` |
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
  `dimensions` (Ume.Rag never sends it to the real BGE-M3).
- **Similar texts get similar vectors**: feature hashing of lower-cased word unigrams and character trigrams with
  FNV-1a 64 (the same function as `RouteSelector.AffinityHash`, `RouteSelector.cs:100-110`), bucket = hash mod n, sign
  from one hash bit, then L2-normalised. Deterministic across processes and platforms.
- **`encoding_format: "base64"`**: little-endian float32 bytes, base64. Several OpenAI SDKs ask for base64 by default
  (to verify for the .NET SDK Ume.Rag uses); without it the fake would break those clients.
- The gateway passes `dimensions` through unchanged (JSON passthrough, ADR 006; no rewrite of it in `src/`).
- Tests: a FakeLlm unit test (new `tests/Ume.LlmGateway.Gateway.Tests/FakeLlmEmbeddingTests.cs` via
  `WebApplicationFactory` of FakeLlm, or a small `FakeLlm.Tests`): default unchanged, dimensions honoured, suffix,
  cosine(similar) > cosine(unrelated), base64 round trip.
- Ships in **B**: no schema, dev-only, and it unblocks Ume.Rag's local development earliest
  (`../platform/repo-and-deployment.md` §4.4).

## 7. Small additions (plan §3a)

### 7.1 Run grouping headers (release A)

- `x-ume-run-id` and `x-ume-step`: any key may send them. Trimmed; at most 100 characters kept (longer values are
  truncated, never refused: metadata must not fail a model call); control characters replaced
  (`SecurityEvents.Printable`, `SecurityEvents.cs:30-49`, which allocates nothing when there is nothing to replace).
- Stored on `UsageRecord` as `RunId varchar(100) NULL`, `Step varchar(100) NULL` (migration `UsageDelegationAndRuns`,
  §5.5). Not in monthly totals (retention summarises without them). No index: the admin filter `runId=` on
  `/api/usage/requests` runs inside the time range like the other filters.
- Exposed on `/v1/usage/records` (`runId`, `step`) and in the admin request list (column hidden by default).
- Documented as **opaque ids only**: no names, no content, no user ids (ADR 003). Ume.Rag sends GUIDs and step names
  such as `condense`, `answer`, `judge`.
- Placed in **A** so `UsageRecords` gets one migration and one COPY change for `Delegated`, `RunId` and `Step`.

### 7.2 Tool passthrough guarantee (release B, tests only)

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
element. Shipped with B because B touches routing and these tests guard the paths Ume.Rag depends on from the start.

## 8. Data model, migrations, Redis

### 8.1 Migrations

| Release | Migration | DDL | Compatible with the previous version because |
|---|---|---|---|
| C | `DepartmentMemberGroups` | `Departments.MemberGroups varchar(256)[] NOT NULL DEFAULT '{}'` | additive with default; old code does not select it |
| A | `KeyDelegation` | `VirtualKeys.DelegatedTeamIds uuid[] NOT NULL DEFAULT '{}'` | same; an old admin API updating a key leaves the column untouched |
| A | `UsageDelegationAndRuns` | `UsageRecords.Delegated boolean NOT NULL DEFAULT false`, `RunId varchar(100) NULL`, `Step varchar(100) NULL` | old usage writer's COPY names its columns (`UsageRecordCopy.cs:27-28`), new columns take their defaults |

Grant changes only in A (§5.9). The migration service re-applies `app-roles.sql` on every start
(`deploy/postgres/app-roles.sql:1-5`).

### 8.2 Redis

| Key | Release | TTL | Value |
|---|---|---|---|
| `ume:groups:{oid}` | C | 10 min | `{ checked, member }` group ids (§4.4) |

No new keys for A or B: delegated budgets use the existing `ume:spend:*`/token counters of the effective team and
department, rate limits the existing per-key keys.

## 9. Settings

| Setting | Env | Release | Default |
|---|---|---|---|
| `Oidc:GroupLookup` | `UME_OIDC_GROUP_LOOKUP` | C | `Claims` |
| `Oidc:TenantId` | `UME_OIDC_TENANT_ID` | C | – |
| `Oidc:RefuseGuests` | `UME_OIDC_REFUSE_GUESTS` | C | `true` |
| `DataApi:TenantId` | `UME_DATA_TENANT_ID` | C | `UME_OIDC_TENANT_ID` |
| `DataApi:RefuseGuests` | `UME_DATA_REFUSE_GUESTS` | C | `UME_OIDC_REFUSE_GUESTS` |

B and A need no settings: the ceiling is per request, delegation per key. No `Gateway:*` setting is added; header
length limits (100) and the delegation list limit (1 000) are constants, documented.

## 10. Security events

Ids follow `SecurityEvents.cs` (1xxx gateway, 2xxx admin API, 3xxx Data API; 1004 is unused and stays unused).
Names and attributes are only added (`docs/data-access.md:58-62`).

| Id | Name | Release | When | Attributes |
|---|---|---|---|---|
| 1002, 1003, 1005 | existing | A | – | **+ `Delegated`**; `TeamId`/`DepartmentId` = effective team |
| 1006 | `gateway.delegation.refused` | A | §5.6 | `RequestId`, `Reason`, `OnBehalfOf`, `Endpoint`, `KeyId`, `KeyPrefix`, `TeamId`, `DepartmentId` |
| 2002 | `admin.signin` | C | – | **+ `DepartmentLookup`** |
| 2007 | `admin.group_lookup.failed` | C | Graph failure at sign-in | `Actor`, `Operation`, `Status` |
| 2008 | `admin.signin.refused` | C | tenant or guest refusal | `Reason` (`wrong_tenant`, `guest_refused`), `Actor`, `SourceAddress` |
| 3003 | `data.token.refused` | C | Data API token refused by tenant/guest check, once a minute per (client, reason) | `Reason`, `Client`, `Count` |

SIEM alerts to add to `docs/security-events.md:28-34`: any 1006 with `team_not_listed` (a delegating key probing
teams, or a caller out of sync); 2007 bursts (Graph outage or permission removed); any 2008 (a guest or foreign
account was assigned to the application).

## 11. Performance and benchmarks

The gateway's rule: no Postgres on the request path with warm caches, 2 Redis round trips before the first byte, 1
after (`docs/performance.md:9-21`); allocation and call counts are the regression gates
(`docs/performance-improvement-plan.md:94-101`).

| Feature | Hot-path cost (warm caches) | Allocations | Round trips |
|---|---|---|---|
| B, header absent | 1 header lookup | 0 B | unchanged |
| B, header present | + span compare of ≤ 8 chars, bit-mask intersection, precomputed array | 0 B | unchanged |
| A, header absent | 1 header lookup | 0 B | unchanged |
| A, header present | + `FrozenDictionary` lookup (code) or `Guid.TryParseExact` + lookup | 0 B (views built at cache load) | unchanged: same Lua scripts, 1–3 more counters in the budget script |
| A, response header | 1 header write (team id string cached on the view) | 0 B | – |
| Run headers | 2 header lookups; `Printable` | 0 B unless a value is over 100 characters or has control characters | – |
| Usage record | + 3 COPY values in the background writer | – | – |
| C | admin sign-in only (1 query, ≤ 1 Graph call, 1 Redis get/set) | – | none on the data plane |

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
| `benchmarks/compare` | not rerun for these releases unless `PipelineBenchmarks` moves; the 2026-10-27 baseline stays the published one |

Compatible with improvement-plan item 2 (one Redis round trip, `docs/performance-improvement-plan.md:56-70`): the
effective team is known right after authentication, before the rate-limit call.

## 12. Gateway docs to update

| Page | Release | Change |
|---|---|---|
| `docs/admin-api.md` (data plane `:360-415`, keys `:95-125`, organisation `:73-94`) | B, C, A | request headers `x-ume-max-residency`, `x-ume-on-behalf-of`, `x-ume-run-id`, `x-ume-step`; response headers incl. the missing `x-ume-residency` and the echo; codes `max_residency_not_met`, `delegation_not_allowed`; `/v1/models` with a ceiling; `memberGroups`; `PUT /api/keys/{id}/delegation`; `delegatedTeamIds`; `/bff/user` fields; directory group endpoints |
| `docs/routing-rules.md` (`:181-206`) | B, A | ceiling as a hard constraint after rules (*Rules never widen access*); example rule on the header; team/department of a delegated request |
| `docs/providers-and-models.md` | B | residency section: what callers can enforce per request, what `x-ume-residency` reports |
| `docs/identity-and-roles.md` (`:26-35`, Entra `:160-186`, check `:217-229`) | C, A | department scope from member groups; Entra step "member groups" with `GroupMember.Read.All` and `UME_OIDC_GROUP_LOOKUP`; tenant and guest refusal; check-the-setup steps for `departmentSources`; delegating keys editable by gateway admins only |
| `docs/data-access.md` (events `:64-77`, permissions `:159-162`, aggregate rule `:237`) | C, A | events 1006, 2007, 2008, 3003, new attributes; new fields; delegated use and `MinimumGroupSize` |
| `docs/security-events.md` (`:20-46`) | C, A | SIEM alerts of §10; guests refused by default |
| `docs/runbook.md` (*Identity provider* `:68-121`, *Data API* `:342-372`) | C | Graph setup, troubleshooting `departmentLookup: failed`, tenant/guest settings |
| `docs/admin-ui.md` | C, A | Organisation member groups; key delegation section; usage *delegated* filter and run id column |
| `docs/performance.md` | A | delegated key cold load (+1 statement); new benchmark case |
| `docs/upgrade-guide.md`, `CHANGELOG.md` | B, C, A | §13 |
| `docs/adr.md` | B, C, A | rows 018, 019, 020 (`adr-drafts.md`) |
| `docs/local-development.md` | B, C | FakeLlm dimensions; Keycloak `group-member` user |
| `README.md` header list, if it repeats the response headers | B | as admin-api |

## 13. Release plan

### 13.1 Release B: residency ceiling

- **Contents**: §3; FakeLlm §6; tool passthrough tests §7.2; ADR 018; docs.
- **Migration**: none. **Rolling update: yes.** While old instances run they ignore the header: callers must check
  `x-ume-residency` (Ume.Rag does, R4).
- **Upgrade-guide outline**: *Rolling update: yes, no migration.* New request header and error code; `/v1/models`
  honours it; `x-ume-residency` now documented; FakeLlm takes `dimensions` / `fake-embed-<n>` (development only).
  Nothing changes for clients that do not send the header.
- **Ume.Rag can then**: send `x-ume-max-residency` (`Eu` for internal, `OnPrem` for sensitive) on every model call;
  stop requiring the second, on-prem-only linked key per team (`GatewayKeyLinks.Purpose = onprem`, data-model §2.1):
  the per-request ceiling replaces the key-level guarantee that R4 found fragile; keep the response check. Developers
  can run against a real local gateway with FakeLlm `fake-embed-1024`.

### 13.2 Release C: department scope from groups, tenant and guest refusal

- **Contents**: §4; ADR 019; settings in compose, Portainer and Kubernetes examples; Keycloak realm; docs.
- **Migration**: `DepartmentMemberGroups`. **Rolling update: yes.** During the window an old admin API instance signs
  users in without group-derived scope and without the tenant/guest checks; sessions are fixed at sign-in, so affected
  admins sign in again after the rollout.
- **Upgrade-guide outline**: *Check before upgrading*: guests are now refused at sign-in by default (`acct = 1` or a
  foreign `idp`): set `UME_OIDC_REFUSE_GUESTS=false` to keep admitting them; set `UME_OIDC_TENANT_ID` on Entra.
  *Opt in*: member groups on *Organisation*; for groups not in the token, `UME_OIDC_GROUP_LOOKUP=Graph`, the
  registration's client secret, `GroupMember.Read.All` with admin consent, egress to Graph. New `/bff/user` fields,
  Data API `memberGroups` and team `code`, events 2007/2008/3003 and attribute `DepartmentLookup`.
- **Ume.Rag can then**: run `OrgSync` with a `catalog.read` Data API key and use `memberGroups` and team codes; switch
  on group-derived department scope in its own BFF (identity design §2); set the same `TenantId`/`RefuseGuests`.

### 13.3 Release A: delegated attribution and run grouping

- **Contents**: §5, §7.1; ADR 020; docs.
- **Migrations**: `KeyDelegation`, `UsageDelegationAndRuns`; `app-roles.sql` grant. **Rolling update: yes**, with three
  rules in the upgrade guide:
  1. Give a key a delegation list only after every gateway instance runs the new version: an old instance ignores
     `x-ume-on-behalf-of` and charges the platform team (callers detect it: no echo header).
  2. Do not rotate a delegating key until the admin API is fully upgraded: an old instance's rotation does not copy
     the list.
  3. A usage-journal batch written by a new instance and replayed by an old one (only after a database outage during
     the rollout, `UsageWriter.cs:288`, `:373`) loses `delegated`, `runId` and `step` for those records.
- **Upgrade-guide outline**: *Rolling update: yes* (rules above); new key setting for gateway admins; delegating keys
  editable by gateway admins only; team deletion refused while a key acts for the team; usage and Data API fields;
  event 1006 and attribute `Delegated`; new run headers (opaque ids only).
- **Ume.Rag can then**: create the platform service key (owned by the platform team, `AllowedResidencies` per
  platform plan, list = teams with assistants), send `x-ume-on-behalf-of: <team id>` and the run headers, verify the
  echo (fail closed when it is missing, like the residency check), and retire `GatewayKeyLinks`: stop reading it at
  once, drop the table one release later (rollback path).

### 13.4 Why this placement

FakeLlm and the tool tests go in B because neither touches schema and both are wanted earliest by Ume.Rag's
development. Run grouping goes in A because it shares the `UsageRecords` migration, the COPY change and the Data API
field change with `Delegated`, so `UsageRecords` changes once.

## 14. Changes to other documents in this repo

All applied in the step 4 review commit (2026-10-29).

1. **`plan.md`** (this folder)
   - §2: header value is a team id or code, Ume.Rag sends the id; response echo `x-ume-on-behalf-of`; request policy is
     the effective team's with the key's on top; budgets per answered question 3 as in §5.4; delegating keys editable
     by gateway admins only; team deletion refused while listed; usage record `Delegated`; "unknown or unlisted → 403"
     is `403 delegation_not_allowed` + event 1006.
   - §3: `x-ume-max-residency: OnPrem | Eu | External` (case-insensitive, no `Any`: omit the header); `403
     max_residency_not_met`; also live sessions, audio, images and `/v1/models`.
   - §3b: no GIN index in the gateway; no MSAL; refusal by redirect; events 2008 and 3003; `code` on catalog teams.
   - §3c: model-name suffix `fake-embed-<n>`, `encoding_format: base64`; ships in B.
   - §3a: run headers in A, truncated not refused; tool tests in B.
   - §5: point to `design.md` for the work items; §6: P1 = three releases B → C → A with the switch-over points of
     §13.
2. **`../identity/design.md`**
   - §1.7: refusal ends the callback with a redirect to an explanatory page, not a bare 403 (same in both systems);
     gateway events 2008/3003 named.
   - §2.4: note that the gateway uses a plain token client (no MSAL); `GroupLookup = Graph` requires `TenantId`.
   - §2.7: item 1 without GIN index; item 4 adds 2008; item 5 adds `code` on `/v1/catalog/teams`; link to this design.
   - §4.7: header values `Eu` / `OnPrem` (canonical spelling); `x-ume-on-behalf-of` = the assistant's team **id**;
     compare `x-ume-residency` case-insensitively by enum order; check the echo header.
   - §10: map RAG 5001 `wrong_tenant`/`guest_refused` to gateway 2008, RAG 5007 to gateway 2007.
3. **`../chat-api/plan.md`** §4 steps 4–5: `Eu` for `internal`, `OnPrem` for `sensitive`; team id; residency check is
   `OnPrem < Eu < External`, case-insensitive (the gateway writes `Eu`, not `EU`); missing `x-ume-on-behalf-of` echo
   aborts like a wrong residency; interim text: after B only the default linked key is needed.
4. **`../platform/plan.md`** §6 residency matrix (`x-ume-max-residency: Eu`) and §7 (team id; B removes the `onprem`
   linked key; A removes linked keys).
5. **`../platform/repo-and-deployment.md`**
   - §4.3 FakeGateway: residencies `OnPrem`, `Eu`, `External` (not `Global`); unknown ceiling value → 400; no model
     within the ceiling → `403 max_residency_not_met`; echo `x-ume-on-behalf-of`; `/v1/catalog/teams` with `code`.
   - §4.4: with gateway release B, add FakeLlm model `fake-embed-1024` (or Ollama `bge-m3`) behind alias `bge-m3`;
     §12.2 feature request: delivered in B.
   - Optional: align the FakeGateway's demo member group with the gateway's dev realm (department 3000 *Individ- och
     familjeomsorgen*, group `IFO-medarbetare`).
6. **`../platform/data-model.md`** §2.1 `GatewayKeyLinks`: `Purpose = onprem` unused after B; table retired after A
   (one release kept for rollback); `Teams.Code` sourced from `/v1/catalog/teams` `code` (release C).
7. **`../../01-general-idea.md`** §10 Decisions table, row "These services → gateway": team **id** in
   `x-ume-on-behalf-of`; ceiling values `OnPrem`/`Eu`; gateway releases B → C → A.
8. **`../../HANDOVER.md`**: step 4 state and commit; next step 5.
9. **`../postponed-decisions.md`**: nothing (no postponement made here).

## 15. Answered questions (2026-10-29)

1. **Guests refused by default for every adopter**: yes, `Oidc:RefuseGuests` and `DataApi:RefuseGuests` default to
   `true`, with a prominent *Check before upgrading* in the upgrade guide (§13.2).
2. **Delegating keys editable by gateway admins only**: yes, every change except rotate and revoke (§5.7).

## 16. To verify

- `TokenValidatedContext.Properties.Items` set in `OnTokenValidated` reach the cookie ticket and
  `CookieSigningInContext.Properties` (expected from the OIDC handler's sign-in, not checked in code).
- `ctx.Fail(...)` in `OnTokenValidated` + `OnRemoteFailure` with `HandleResponse()` gives a clean redirect on .NET 10.
- EF Core maps the new non-mapped `VirtualKey` members only if ignored explicitly; whether `GatewayDbContext`
  (`:62-70`) needs `e.Ignore(...)` for `OwnerTeam`.
- Graph: `checkMemberGroups` on `/users/{id}` with `GroupMember.Read.All` only (identity design §16), and
  `GET /groups?$search` with that permission.
- Entra `acct` and `idp` values for B2B guests and members in ID tokens and in delegated access tokens for the Data
  API; whether a member's ID token ever carries `idp` equal to the v1 issuer form.
- Docker Compose nested default `${A:-${B:-true}}` (one level is already used at `deploy/compose.prod.yaml:181`).
- The OpenAI .NET SDK's default `encoding_format` for embeddings (base64 or float).
- Ollama's OpenAI-compatible `/v1/embeddings` and a `dimensions` parameter (only matters if a caller sends it to real
  BGE-M3; Ume.Rag does not).
- That no gateway code path reads `key.TeamId` meaning "the key's owner" after step 1b besides those listed in §5.1
  (grep at implementation time; `AuthFailureRecorder.cs:57` runs before step 1b and keeps the owner).
- Realtime: whether WebSocket clients used by adopters can set custom headers on the upgrade request (server SDKs can;
  browsers cannot, which only matters for B and A, not for Ume.Rag).
