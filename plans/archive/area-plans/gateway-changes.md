# Gateway changes: plan

**Detailed design: [`../../gateway-changes/design.md`](../../gateway-changes/design.md)** (step 4) and ADR drafts 018–020 in [`../../gateway-changes/adr-drafts.md`](../../gateway-changes/adr-drafts.md);
where they differ from this plan, they win (header values, error codes, the team id, events, the release).

Small features in the LLM gateway (`~/git/llmgateway`, our own code), so the RAG platform does not have to store
one gateway key per team, and so the `sensitive` → on-prem rule is enforced by the gateway on every request rather
than by key settings checked once (review R4, `../02-architecture-review.md`). The letters A, B and C below name
features, not releases. They ship in **two releases** (reasoning: [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):

- **Release 1, in P1b before the pilot goes live**: feature B (§3) and the FakeLlm `dimensions` addition (§3c);
  ADR 018.
- **Later (P2): release 2**: feature C (§3b) and feature A (§2), before the first second-department or group
  audience; ADRs 019–020.

P1a runs against the gateway as it is (v0.8.5) on an interim test/dev instance (§4). Until release 2 the pilot's calls
are attributed with the **per-level gateway keys of the pilot department's team** (§4).

## 1. Why

- Today a gateway key belongs to one team, and the gateway has no way for a caller to act for another team.
  Attribution would require the RAG platform to hold a key per team (two for teams with sensitive assistants):
  many secrets to store and rotate.
- A key's residencies can be changed later by its team admins (`docs/identity-and-roles.md`). A "sensitive key"
  verified once at link time can silently be widened. The RAG platform therefore holds one key per level (the
  pilot team's until release 2, delegating keys after) and sends an explicit ceiling on every call; the gateway
  enforces both from release 1 (§3).

## 2. Feature A: delegated attribution (release 2, P2)

**Later (P2).**

- A key can be marked **delegating** by a gateway admin (only gateway admins), with an explicit **list of teams** it
  may act for (decision: listed teams, not whole departments); new teams are added explicitly.
- Requests from such a key may carry `x-ume-on-behalf-of: <team id or code>` (Ume.Rag sends the id; codes can change).
  The gateway then:
  - applies that team's budgets, quotas and routing rules, and the key's own allowed models/residencies (the
    **intersection**: delegation can never widen access);
  - applies **only the key's request policy**, never the effective team's (key flag `ApplyTeamPolicy`, default false
    for delegating keys; design §5.3);
  - records usage against the team, with the delegating key id (no content and no end-user id: per-user limits and
    statistics stay in the RAG platform);
  - refuses unknown or inactive teams and teams outside the delegation list (`403 delegation_not_allowed`, event 1006);
  - echoes `x-ume-on-behalf-of` so the caller can verify delegation was applied;
  - every change to a delegating key is gateway-admin only (rotate/revoke excepted); a listed team cannot be deleted.
- Without the header the key behaves as a normal key of its own team.
- Admin UI: the key page shows the delegation scope; usage reports can be filtered by delegating key.

## 3. Feature B: per-request residency ceiling (release 1, before the pilot)

- Header `x-ume-max-residency: OnPrem | Eu | External` (the `DataResidency` names, case-insensitive; omit for no
  ceiling; malformed `400 invalid_request`). It removes every candidate provider above the ceiling before routing,
  after routing rules and fallback lists are computed, like the existing hard constraints in `IRouteResolver`. It can
  only narrow.
- Key flag **`RequireMaxResidency`**: the gateway refuses a request from that key without the header
  (`400 invalid_request`). Every Ume.Rag key has it.
- No configured target within the ceiling: `403 max_residency_not_met` (no silent fallback to a wider residency).
  Targets within the ceiling exist but are drained or circuit-open: `503 no_eligible_provider` with `Retry-After`.
- The response's existing `x-ume-residency` header (`GatewayHeaders.cs:15`) reports where the request actually went,
  so callers can verify (the Chat API does, before streaming).
- Applies to every endpoint: chat, responses, messages, embeddings, audio, images, realtime and `GET /v1/models`.
- **`GET /v1/models` at a ceiling**: today it lists the models the key may use, filtered by the key's own allowed
  models, providers and residencies (and enabled, not drained providers), with no request-side filter
  (`GatewayRequestHandler.Models.cs:22`). The release intersects that list with the request's `x-ume-max-residency`
  for every key, delegating keys included, and `ume.residencies` then shows only residencies within the ceiling.
  Ume.Rag's model check on assistant save relies on this (`../../admin/design.md` §4.6 V8; design §3.4).

## 3b. Feature C: department scope from groups (shared with the RAG platform; release 2, P2)

**Later (P2).** Both systems get a user's department scope only from the `departmentCodes` claim, and the municipality has no
department attribute in Entra today. Added to the gateway and implemented identically in the RAG platform
(`identity.md` §3), **claims only**:

- A department gets an optional list of **member groups** (`MemberGroups varchar(256)[]`: group claim values, i.e.
  Entra object ids or Keycloak/AD FS group names, matched case-insensitively like `Oidc:RoleGroups`), edited on the
  *Organisation* page by gateway admins.
- At sign-in, department scope = `departmentCodes` claim values **plus** departments whose member groups appear in the
  user's group claim. Roles still come from `roles` (or `UME_OIDC_GROUPS_<ROLE>`).
- No change for adopters that have a department attribute: the claim alone still works.
- The member groups travel with the organisation data the RAG platform reads from the gateway, so both systems
  always agree on who belongs to which department: the Data API's `catalog.read` endpoint
  `GET /v1/catalog/departments` gains a `memberGroups` field (the RAG admin syncs from it every 5 minutes with a Data
  API key, `catalog.read` only).
- `/bff/user` gains `departmentSources` (claim or group per code).
- Full design, shared by both systems (`DepartmentMembershipResolver`, work items, tests):
  `../../identity/design.md` §2.

**Small item with feature C: tenant and guest refusal.** `UME_OIDC_TENANT_ID` (when set, `tid` must match) and
`UME_OIDC_REFUSE_GUESTS` (default on: refuse `acct = 1` or a foreign `idp`, one rule in both systems,
`../../identity/design.md` §1.7) for admin sign-in and the Data API.

## 3c. FakeLlm: honour the `dimensions` parameter (release 1)

The gateway's FakeLlm returns 8-dimensional embeddings (`Vector()` in `src/Ume.LlmGateway.FakeLlm/Program.cs`), so the
RAG platform (1024-dimension profiles: the pilot's EU `text-embedding-3-large` at 1024, later on-prem BGE-M3) cannot
run against a real local gateway with the fake provider. Small change:
honour OpenAI's optional `dimensions` request parameter (default stays 8), using deterministic feature hashing so
similar texts get similar vectors. Until then, developers add Ollama `bge-m3` to the local gateway
(`../../platform/repo-and-deployment.md` §4.4).

## 3a. Small additions (from the Bifrost analysis)

- **Run grouping headers** (**later (P2)**: metadata only; release 2, with feature A's usage migration): `x-ume-run-id` and `x-ume-step` stored on usage records, so the many
  model calls of one agent run, ingestion run or evaluation run can be grouped and costed together. No content.
- **Tool passthrough guarantee** (release 1, tests only): tests asserting that `tools`, `tool_choice` and tool messages pass through the
  gateway unchanged and in the same order on every endpoint, including the translated ones (Anthropic ↔ OpenAI,
  Responses API).
- **Optional, later release** (design §7.3): `contextWindow` and `features` (incl. `tools`) on `/v1/catalog/models`, so
  the RAG admin can prefill an assistant's context size (chat design §6.3) and set `GatewayModels.SupportsTools`
  (tools design §6.4), and `GET /v1/catalog/routes` (route aliases with their target models), so route aliases need no
  manual `GatewayModels` rows (admin design §4.10). Not needed for P1: the admin's P1 `GatewayModels` sync keeps
  manual values and rows until then.

**Placement decision: MCP and tools stay out of the gateway.** Bifrost runs MCP inside its gateway; for us the tool
hub belongs in the RAG platform, because the gateway only knows team keys (not users), never sees the user's Entra
token (needed for OBO), stores no content by design, and should stay small and fast. The gateway's job for tools is
to pass them through faithfully and account for the calls.

## 3d. P2: rerank endpoint (separate release)

**Later (P2).** Not part of release 1 or release 2. The gateway has no rerank endpoint today; Ume.Rag's `IReranker` seam
(`../../knowledge-api/design.md` §6.7) runs against a FakeGateway rerank endpoint in P1. For P2, a separate gateway
change adds a rerank endpoint serving `bge-reranker-v2-m3` on-prem: query plus passages in, one score per passage out,
honouring `x-ume-max-residency` and `RequireMaxResidency` like every other endpoint, with usage recorded per key and
team. Path and request shape are to be defined with the gateway team (to verify); the FakeGateway mirrors them.

## 4. Interim without the gateway changes

CI and tests use the FakeGateway. **P1a** calls the gateway as it is (v0.8.5) on an interim test/dev instance with
EU-hosted embedding and chat models (Decisions "Interim gateway for P1a", "P1a plan answers"; design §13.4): keys
enforce `AllowedResidencies`, and Ume.Rag sends the ceiling anyway and checks `x-ume-residency` on every answer. The
pilot goes live only after **release 1** (P1b). Until release 2, **pilot attribution** uses the pilot department's
team's own gateway keys, one per level (`OnPrem`-only, `Eu`-max, open), issued per deployable with `AllowedIpRanges`
and (from release 1) `RequireMaxResidency`. These are the only per-team keys Ume.Rag holds; the gateway is unchanged
by them (they are ordinary team keys). Every P1 call is then charged to the pilot team, and Ume.Rag sends no
`x-ume-on-behalf-of`. Because the keys are not delegating, the gateway also applies the pilot team's request policy,
so that team keeps no request policy while its keys serve Ume.Rag. With release 2, Ume.Rag switches to delegating
keys per level (design §13.4) and the pilot team's keys are revoked.

## 5. Work items (gateway repo)

Detailed per file in `../../gateway-changes/design.md` (§3.5, §4, §5, §6, §7); the release plan is design §13. Release in brackets.

1. Domain: residency ceiling parsing and the key flag `RequireMaxResidency` (release 1); delegation scope and the key
   flag `ApplyTeamPolicy` (release 2).
2. Request pipeline: header check after HMAC authentication; ceiling filter in route resolution with the 403/503
   rule (release 1); resolve the effective team from the header, budget reservation against that team, request
   policy per `ApplyTeamPolicy` (release 2).
3. Usage records: delegating key id, effective team, run grouping (release 2).
4. Admin API/UI: `RequireMaxResidency` on the key form (release 1); delegation scope and `ApplyTeamPolicy` (gateway
   admins only on delegating keys), audit entries, member groups on *Organisation* (release 2).
5. Tests: ceiling removes all candidates, drained in-ceiling target → 503, fallback never exceeds the ceiling,
   `/v1/models` at a ceiling, benchmarks unchanged (release 1); intersection rules, unknown team, budgets charged to
   the right team, team policy not applied (release 2).
6. Docs: routing rules, identity and roles, ADR 018 (release 1), ADRs 019–020 (release 2).
7. Feature C (claims only) and tenant/guest refusal: the work items in `../../identity/design.md` §2.7 (release 2).
8. P2, separate release: the rerank endpoint (§3d).

## 6. Milestones

| Phase | Deliverable |
|---|---|
| P0 | ADRs and design agreed in the gateway repo; release 1 scheduled before the pilot |
| P1 | P1a against v0.8.5 on the interim test/dev gateway (§4). **Release 1** (design §13) in P1b before go-live: Ume.Rag sends the ceiling on every call with the pilot team's per-level keys (§4) and validates models against `/v1/models` at a ceiling. CI and tests run on the FakeGateway |
| P2 | **Later: release 2** (design §13), before the first second-department or group audience: Ume.Rag switches to its per-level delegating keys, sends `x-ume-on-behalf-of` and syncs member groups. Rerank endpoint for `bge-reranker-v2-m3` on-prem (§3d), its own release |

## 7. Open questions

Answered (2026-10-29, release order revised 2026-10-30):

1. **Delegation scope**: an explicit list of teams.
2. **End-user id at the gateway**: no. The gateway only sees teams; per-user rate limits and statistics are done in
   the RAG platform.
3. **Budgets under delegation** (2026-10-29): a delegated request is checked against and charged to the effective
   team's and its department's budgets, **and** the delegating key's own key budget and rate limit apply as an
   overall cap on the platform key.
4. **Who may send `x-ume-max-residency`**: any key (it can only narrow); documented as a general gateway feature.
   Keys with `RequireMaxResidency` must send it.
5. **Release order**: two rolling-update-safe releases: release 1 (B and FakeLlm `dimensions`, ADR 018) before the
   pilot; release 2 (C claims only and A, ADRs 019–020) in P2 (weakness review §5).
6. **Step 4 output**: drafts in this repo (`plans/gateway-changes/`: ADR rows in the gateway's table format + a design
   document), copied into the gateway repo when implementation starts.
7. **Guests refused by default** for every gateway adopter (`RefuseGuests = true`), with an upgrade-guide note.
8. **Delegating keys** are editable by gateway admins only (rotate and revoke excepted).

## 8. Deferred or removed (simplification pass)

Per [`../passes/simplification-pass.md`](../passes/simplification-pass.md) §6–§7:

| Item | Where it went |
|---|---|
| Three releases B → C → A | two releases: B; then C and A (§6, design §13) |
| Interim per-team linked keys (`GatewayKeyLinks`, `onprem` keys) | removed; FakeGateway in CI and tests, the interim test/dev gateway in P1a, the pilot team's per-level keys until release 2 (§4) |
| Graph `checkMemberGroups` lookup, `GroupMember.Read.All`, membership cache, circuit, event 2007, `departmentLookup` and its banner | P2, with data-plane audiences and approval package 2 (identity plan §2) |
| Effective team's request policy on delegated requests | removed; key's policy only unless `ApplyTeamPolicy` |
| `403 max_residency_not_met` for drained in-ceiling targets | replaced by `503` with `Retry-After` |

## 9. Deferred or removed (weakness review)

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| One combined release before the pilot | release 1 (feature B, FakeLlm `dimensions`, ADR 018) before the pilot; release 2 (features C and A, ADRs 019–020) in P2 |
| Delegated attribution for the pilot (delegating keys, `x-ume-on-behalf-of`, echo check) | P2 with release 2; the pilot uses the pilot team's per-level keys (§4) |
| Member groups from claims and tenant/guest refusal in the gateway | P2 with release 2 |
| Run grouping headers stored on usage records (§3a) | release 2 (migration `UsageDelegationAndRuns`); a release-1 gateway ignores them |
