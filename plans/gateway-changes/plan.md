# Gateway changes: plan

Two small features in the LLM gateway (`~/git/llmgateway`, our own code) so the RAG platform does not have to store
one gateway key per team, and so the `sensitive` → on-prem rule is enforced by the gateway on every request rather
than by key settings checked once (review R4, `../../02-architecture-review.md`).

## 1. Why

- Today a gateway key belongs to one team, and the gateway has no way for a caller to act for another team.
  Attribution would require the RAG platform to hold a key per team (two for teams with sensitive assistants):
  many secrets to store and rotate.
- A key's residencies can be changed later by its team admins (`docs/identity-and-roles.md`). A "sensitive key"
  verified once at link time can silently be widened.

## 2. Feature A: delegated attribution

- A key can be marked **delegating** by a gateway admin (only gateway admins), with an explicit **list of teams** it
  may act for (decision: listed teams, not whole departments); new teams are added explicitly.
- Requests from such a key may carry `x-ume-on-behalf-of: <team code>`. The gateway then:
  - applies that team's budgets, quotas and request policies, and the key's own allowed models/residencies, taking
    the **intersection** (delegation can never widen access);
  - records usage against the team, with the delegating key id (no content and no end-user id: per-user limits and
    statistics stay in the RAG platform);
  - refuses unknown team codes or teams outside the delegation list (403, security event).
- Without the header the key behaves as a normal key of its own team.
- Admin UI: the key page shows the delegation scope; usage reports can be filtered by delegating key.

## 3. Feature B: per-request residency ceiling

- Header `x-ume-max-residency: OnPrem | EU | Any` (names to match the gateway's residency enum). It removes every
  candidate provider above the ceiling before routing, after routing rules and fallback lists are computed, like the
  existing hard constraints in `IRouteResolver`. It can only narrow.
- If no candidate remains: 4xx with a clear error code (no silent fallback to a wider residency).
- The response's existing `x-ume-residency` header (`GatewayHeaders.cs:15`) reports where the request actually went,
  so callers can verify (the Chat API does, before streaming).
- Applies to every endpoint: chat, responses, messages, embeddings.

## 3b. Feature C: department scope from groups (shared with the RAG platform)

Both systems get a user's department scope only from the `departmentCodes` claim, and the municipality has no
department attribute in Entra today. Added to the gateway first, and implemented identically in the RAG platform
(`../identity/plan.md` §3):

- A department gets an optional list of **member groups** (`MemberGroups varchar(256)[]`: group claim values, i.e.
  Entra object ids or Keycloak/AD FS group names, matched case-insensitively like `Oidc:RoleGroups`), edited on the
  *Organisation* page by gateway admins.
- At sign-in, department scope = `departmentCodes` claim values **plus** departments whose member groups the user is
  in. Roles still come from `roles` (or `UME_OIDC_GROUPS_<ROLE>`).
- Membership from the group claim, plus (with `UME_OIDC_GROUP_LOOKUP=Graph`) a Graph `checkMemberGroups` call for
  member groups the claim does not show (overage, or groups not assigned to the app), cached 10 minutes in
  `ume:groups:{oid}` (needs `GroupMember.Read.All` on the admin registration). If Graph fails, sign-in continues with
  claims only (scope can only narrow), `/bff/user` shows `departmentLookup: "failed"` and event 2007
  `admin.group_lookup.failed` is logged.
- No change for adopters that have a department attribute: the claim alone still works.
- The member groups travel with the organisation data the RAG platform reads from the gateway, so both systems
  always agree on who belongs to which department: the Data API's `catalog.read` endpoint
  `GET /v1/catalog/departments` gains a `memberGroups` field (the RAG admin syncs from it every 5 minutes with a Data
  API key, `catalog.read` only).
- `/bff/user` gains `departmentSources` (claim or group per code) and `departmentLookup`; `admin.signin` (2002) gains
  the attribute `DepartmentLookup`.
- Full design, shared by both systems (`DepartmentMembershipResolver`, work items, tests):
  `../identity/design.md` §2.

**Small item with feature C: tenant and guest refusal.** `UME_OIDC_TENANT_ID` (when set, `tid` must match) and
`UME_OIDC_REFUSE_GUESTS` (default on: refuse `acct = 1` or a foreign `idp`) for admin sign-in and the Data API, so a
guest assigned by mistake to the shared registration reaches neither system (`../identity/design.md` §1.7).

## 3c. FakeLlm: honour the `dimensions` parameter

The gateway's FakeLlm returns 8-dimensional embeddings (`Vector()` in `src/Ume.LlmGateway.FakeLlm/Program.cs`), so the
RAG platform (1024-dim BGE-M3 profile) cannot run against a real local gateway with the fake provider. Small change:
honour OpenAI's optional `dimensions` request parameter (default stays 8), using deterministic feature hashing so
similar texts get similar vectors. Until then, developers add Ollama `bge-m3` to the local gateway
(`../platform/repo-and-deployment.md` §4.4).

## 3a. Small additions (from the Bifrost analysis)

- **Run grouping headers** (metadata only): `x-ume-run-id` and `x-ume-step` stored on usage records, so the many
  model calls of one agent run, ingestion run or evaluation run can be grouped and costed together. No content.
- **Tool passthrough guarantee**: tests asserting that `tools`, `tool_choice` and tool messages pass through the
  gateway unchanged and in the same order on every endpoint, including the translated ones (Anthropic ↔ OpenAI,
  Responses API).

**Placement decision: MCP and tools stay out of the gateway.** Bifrost runs MCP inside its gateway; for us the tool
hub belongs in the RAG platform, because the gateway only knows team keys (not users), never sees the user's Entra
token (needed for OBO), stores no content by design, and should stay small and fast. The gateway's job for tools is
to pass them through faithfully and account for the calls.

## 4. Interim without the gateway changes

Per-team gateway keys linked in the RAG admin (encrypted with Data Protection), a separate on-prem-only key for
sensitive use, and the `x-ume-residency` response check in the Chat API and worker. This works with the current
gateway but has the drawbacks above.

## 5. Work items (gateway repo)

1. Domain: delegation scope on keys; residency ceiling parsing.
2. Request pipeline: resolve the effective team from the header after HMAC authentication; budget reservation and
   policies against that team; ceiling filter in route resolution.
3. Usage records: delegating key id and effective team.
4. Admin API/UI: delegation scope (gateway admins only), audit entries.
5. Tests: intersection rules, unknown team, ceiling removes all candidates, fallback never exceeds ceiling, budgets
   charged to the right team, benchmarks unchanged.
6. Docs: routing rules, identity and roles, ADR for delegation.
7. Feature C and tenant/guest refusal: the work items in `../identity/design.md` §2.7 (migration, Organisation page,
   `OidcOptions`, resolver + Graph client, `/bff/user`, event 2007, Data API field, settings, docs, tests).

## 6. Milestones

| Phase | Deliverable |
|---|---|
| P0 | ADR and design agreed in the gateway repo |
| P1 | Features A, B and C released; RAG platform switches from interim keys to the platform service key before the pilot if ready |

## 7. Open questions

Answered (2026-10-29):

1. **Delegation scope**: an explicit list of teams.
2. **End-user id at the gateway**: no. The gateway only sees teams; per-user rate limits and statistics are done in
   the RAG platform.
