# identity: history moved out of the design

The process sections of [`../../identity/design.md`](../../identity/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 14. Changes to other documents

The step 3 changes were applied in the step 3 review commit (2026-10-29). Needed for the simplification pass
(2026-10-30), applied by the owners of those documents:

1. **`../platform/data-model.md`**: §2.1 `Clients` without `ResolveUsersByEmail`, `TaskModel`, `TaskModelResidency`;
   `Departments.MemberGroups` without GIN index; `GatewayKeyLinks` removed; audit actor columns hold `oid` (fallback
   `sub`); §11 Redis without `ume-rag:groups:{oid}` and `ume-rag:owui-oid:*` (P2 or dropped); references to identity
   design §5.4 (e-mail mapping) removed.
2. **`../platform/repo-and-deployment.md`**: §6.1 networks: in P1 the admin needs only `login.microsoftonline.com`
   (no Graph); §8.1 `entra.yml`: Bicep owns only RAG's registrations (§12); §9 events: 5007 reserved (P2), no
   `DepartmentLookup` on 6002; settings `Config:MaxUnconfirmedSeconds` (90), `Gateway:RequireDelegationEcho` (default
   `true`); `Gateway:SendResidencyCeiling` and `Gateway:AttributionMode` removed (the ceiling is always sent, there is
   one attribution mode); §4.2 realm unchanged.
3. **`../archive/area-plans/admin.md`, `../admin/design.md`**: explain-access endpoint and page (§4.9); model validation against
   `/v1/models` on assistant save (§4.7); organisation page shows member groups (entered in the gateway) without a
   lookup banner; roles on `ume-rag-admin`; audit actor `oid`.
4. **`../archive/area-plans/chat-api.md`, `../chat-api/design.md`**: same effective level from turn 1 for history-keeping clients;
   `External` for `public`; `X-Rag-Owui-Task` and `owui-tasks` removed; gateway `503` + `Retry-After` retryable, `403
   max_residency_not_met` not; `Gateway:RequireDelegationEcho` default `true`.
5. **`../archive/area-plans/platform.md`** §7: "gateway budgets, policies and usage reports stay per team" → policies are the key's
   for delegated requests (`ApplyTeamPolicy = false`).
6. **`../to-verify.md`**: row 82 (Graph `checkMemberGroups`) moves to P2; row 84 without `uniqueName` adoption; new
   rows from §16.
7. **`../postponed-decisions.md`**: nothing (E1 already covers Open WebUI group audiences).

Needed for simplification pass §15 (assistant versions, owner-team scoping):

8. **`../platform/data-model.md`** (done in the parallel pass): `Teams.Code` is used for owner-team membership, so it
   is no longer "display only"; `DepartmentSettings.EditOwnerTeamOnly` limits `Edit`, `EditPipelines` and `EditSkills`
   (§1.3).
9. **`../admin/design.md`** (done in the same pass): §3.2 owner-team scoping; publish and restore never change
   audiences or level; test panel and assistant tests run drafts as `admin-console`.
10. **`../gateway-changes/design.md`**: only if `teamCodes` cannot be filled (§16): member groups on teams, as feature C
    for departments, in both systems.

Needed for the Keycloak pilot (Decisions table "P1 pilot sign-in", 2026-10-30; this pass 2026-10-31), applied by the
owners of those documents:

11. **`../archive/area-plans/identity.md`**: the opening paragraph ("Keycloak is used for local development and CI") contradicts its own §1;
    §2's table and package 1 ("submitted during P0, approved before the pilot") → package 1 is P2, staff and pilot
    sign-in in P1 is the shared Keycloak; §3 "Roles from Entra" and "Entra group object ids in the department form"
    → P1 realm roles and Keycloak group names (design §2.2), Entra from P2; the `tid`/guest rule takes effect with
    Entra (§1.7).
12. **`../admin/design.md`**: §1 row 4 and §3.1 (`:60`, `:174`) say roles come from the Entra registration and
    assignments, actor `oid` → P1: realm roles on the Keycloak client `ume-rag-admin`, actor `sub` (§1.1, §1.6).
13. **`../chat-api/design.md`**: §4.3 Open WebUI OIDC against the shared Keycloak (client `open-webui`, provider URL,
    provider name; §5.3), and the to-verify note at `:1168` ("Entra-only login", feedback service account) → a
    Keycloak account in P1; the Open WebUI sign-in gate is open (§16 question 5).
14. **`../platform/repo-and-deployment.md`**: §4.2 adds RAG's `deploy/keycloak/` realm additions next to the dev realm
    (§11.2); §7.1 and checklist #17: package 1 is kept, not submitted (P2), and `init-deployment.sh`'s Entra examples
    and next step ("Register these redirect URIs … in Entra") read Keycloak for P1.
15. **`../platform/data-model.md`**: §2.3 `Conversations.SubjectId` and the audit actor columns hold the Keycloak
    `sub` in P1 (Entra `oid` from P2).
16. **`../to-verify.md`**: new rows for the two P1 items of §16 (full scope off with role scope mappings; partial
    import coverage) and for question 7 (Open WebUI account keying on a provider switch).
17. **`../gateway-changes/prompt-gateway-session.md`** §2 (or the gateway session's follow-up brief): carry §16's P1
    questions 1–4 and 6 to the gateway session, which owns the shared realm.
18. **`../archive/p1/README.md`** item 16: only if question 3 puts the realm-level settings in the gateway's base
    realm, item 16's content and done-when change.

## 15. Answered questions (2026-10-29, revised 2026-10-30)

1. **Registration ownership** (revised in the simplification pass): `ume-rag/infra/entra/` owns only RAG's
   registrations (`ume-rag-admin`, Open WebUI); the gateway's registration stays the gateway's (§12.2).
2. **Admin credential**: a client secret of our own, identical to the gateway's setup; certificates later for both
   systems together (§12.2).
3. **Many assistants per department** (user, 2026-10-30, simplification pass §15): P2 owner-team scoping of admin
   writes, decided here as an admin rule on `teamCodes` (not a role, not an `IAccessPolicy` step) (§1.3); assistant
   versions do not change access, because audience and level belong to the assistant (§4.3).

## 17. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md) §1, §6, §7, §9 and §14:

| Removed or deferred | Where it went |
|---|---|
| RAG admin on the gateway's existing admin registration (own secret, shared roles), the twin fallback, `infra/entra/` owning the gateway's registration | removed: own `ume-rag-admin` registration; Bicep owns only RAG's registrations (§1.6, §12) |
| Graph `checkMemberGroups` lookup (`Oidc:GroupLookup`, `GraphGroupClient`/`IGroupMembershipSource`), `GroupMember.Read.All` in package 1, Redis `ume-rag:groups:{oid}`, circuit, event 5007 (id reserved), `departmentLookup` on `/bff/user`, attribute `DepartmentLookup` on 6002, the "failed" banner, `OrgSync:MaxAgeHours`, group search by name | P2, with data-plane audiences and package 2 (plan §2) |
| `ResolveUsersByEmail` (Open WebUI e-mail → Entra `oid` via Graph, `ume-rag:owui-oid:*`) | dropped; group audiences through Open WebUI wait for E1 (§5.4) |
| Audit actor `sub` | `oid`, fallback `sub` (§1.1) |
| Strict 10 s config confirmation, readiness failing at once | grace window `Config:MaxUnconfirmedSeconds` (90 s) (§4.3 step 0, §4.5) |
| No ceiling header for `public`; interim linked keys and the `onprem` key; "echo required once release A is in use" | `External` always sent, one credential key per service, `Gateway:RequireDelegationEcho` (§4.7) |
| Follow-up-only rule for history-keeping clients | same effective level from the first turn (§4.7) |
| `rag-tools` as a P2 service (mTLS hop from the chat and admin roles, tool credential held by it) | P3 with the first external, OBO or write tool; P2 tool calls run in the in-process hub of `rag-api` and `rag-admin`, which hold the `ume-rag-tools` certificate (§9.2, §12.1) |
| SIEM alert on any 5105; admin audit action `gate.override` | removed (§10) |
| `owui-tasks`, `X-Rag-Owui-Task`, `Clients.TaskModel`/`TaskModelResidency` | removed with Open WebUI task generation (§4.1, §5.1) |
| Guest rule "`idp` ≠ issuer" | one rule in both systems (§1.7) |
| Admin sessions in Redis (`ume-rag:admin-session:{sessionId}`) | Postgres `config."AdminSessions"` (§1.1); no Redis in P1 |

## 18. Deferred or removed (weakness review)

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Removed or deferred | Where it went |
|---|---|
| Group-derived department scope from the gateway's catalogue in P1 (§2) | P2 with gateway release 2; P1 scope from the `departmentCodes` claim plus member groups entered by hand on our department record (weakness review §5, "settled while applying"; Keycloak group names, §2.2) |
| Delegating keys, `x-ume-on-behalf-of`, echo check and event 5011 in P1 (§4.7) | acting as an assigned key from the gateway's integration phase 2; until phase 1 the pilot team's per-level keys |
| Gateway tenant/guest refusal (§1.7) | gateway release 2 (P2); our own refusal is unchanged in P1 |
| E1 test (§9.4) | P2 with Entra (Decisions table "P1b briefs", O3); the W3 spike in P1b; one Open WebUI instance per department until W3 and E1 are solved (§5.2) |
