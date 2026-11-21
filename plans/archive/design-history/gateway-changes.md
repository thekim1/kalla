# gateway-changes: history moved out of the design

The process sections of [`../../gateway-changes/design.md`](../../gateway-changes/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

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

## 17. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md) §6–§7:

| Removed or deferred | Where it went |
|---|---|
| Releases B → C → A (three upgrade windows, per-release upgrade guides, ADR rows per release) | two releases: B in release 1, C in release 2; A in the gateway's integration phases (§5, §13, §18) |
| Graph `checkMemberGroups` lookup (`Oidc:GroupLookup`, `GraphTokenClient`, `IGroupMembershipSource`), `GroupMember.Read.All`, Redis `ume:groups:{oid}`, circuit, event 2007, `departmentLookup` on `/bff/user`, attribute `DepartmentLookup` on 2002, the "failed" banner, directory group search endpoints | P2, with data-plane audiences and approval package 2 (identity plan §2); P1 member groups are entered as group claim values |
| Effective team's request policy on delegated requests | replaced by acting as an assigned key: that key's and its team's request policy apply (§5.3) |
| `403 max_residency_not_met` while in-ceiling targets are only drained | `503 no_eligible_provider` + `Retry-After` (§3.3) |
| Ume.Rag interim: per-team linked keys `GatewayKeyLinks`, `onprem` keys, the "platform service key" (one key) | removed; FakeGateway in CI and tests, the interim test/dev gateway in P1a, the pilot team's keys per level and deployable until the gateway's integration phase 1, then one credential key per service and an assigned key per assistant and knowledge base (§13.4) |

## 18. Deferred or removed (weakness review)

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Removed or deferred | Where it went |
|---|---|
| One combined release before the pilot (B, C, A; ADRs 018–020 together) | release 1 (B, FakeLlm `dimensions`, tool passthrough tests, ADR 018) before the pilot; release 2 (C with tenant/guest refusal, ADR 019) in P2; A and the run headers in the gateway's integration phases (§5, §13) |
| `RequireMaxResidency` in migration `KeyDelegation` | its own migration `KeyResidencyCeiling` in release 1 (§8.1) |
| Ume.Rag on delegating keys from the pilot on | the pilot team's per-level keys until the gateway's integration phase 1 (§13.4) |

## Release plan, as planned (§13, moved 2026-11-21)

Moved here from the design in session 53's clean-up, with the design's header of then. The features shipped: B in
gateway v0.9.0, C's member groups and A's phases 1–3 in v0.10.0, the Integration API's address in v0.10.2.

### The design's header before the clean-up

Detail step 4 of the Källa design. Feature A (§5) is the gateway's integrations design (`docs/integrations.md` in
the gateway repository, decided by user, 2026-11-11, #580); this document keeps only what Källa relies on from it. Details [`../archive/area-plans/gateway-changes.md`](../area-plans/gateway-changes.md) (archived: history, not binding; this design states every rule, the plan's answered questions in §15) and the
gateway side of [`../identity/design.md`](../../identity/design.md) §1.4, §1.5, §1.7, §2 and §10 (feature C and the
tenant/guest refusal are designed there for both systems; this document only turns them into gateway work). The ADR
rows are in [`adr-drafts.md`](../gateway-changes/adr-drafts.md). Nothing here is implemented in the gateway yet (`~/git/llmgateway` is at
v0.8.5); Källa's side of feature B is built and runs against v0.8.5 until release 1 (§13.4).

**As built (#390, 2026-11-06):** release 1 is implemented in gateway draft PRs thekim1/fors#65 (embeddings per
input), #66 (feature B, ADR 018) and #67 (FakeLlm, tool passthrough); rerank is #68 and the shared Keycloak #70. Where
this document and those PRs differ, the PRs win; §3.3, §3.5 and §7.4 below state the built behaviour. Ground rule: the
gateway never depends on Källa; every change is a generic gateway feature.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5), superseded by the PRs above. "Pipeline" means
`src/Fors.Gateway/Pipeline/`; "Entities.cs" means `src/Fors.Domain/Entities/Entities.cs`.

### §13 as planned

### 13.1 Contents

Two releases, so the pilot waits only for the smallest change a production gateway that other teams use must take
(reasoning: [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):

- **Release 1, in P1b before go-live** (built in a separate session in the gateway repo): §3 (feature B), §6 (FakeLlm `dimensions`), §7.2 (tool passthrough tests), ADR 018,
  the docs of §12 for B, and the upgrade-guide entry (§13.3).
- **Later (P2): release 2**, before the first second-department or group audience: §4 (feature C with the tenant/guest
  refusal), ADR 019, the remaining docs of §12, settings in compose, Portainer and Kubernetes examples, and the
  Keycloak realm.
- **Feature A** (§5, with §7.1) comes in the gateway's integration phases 1–3, each its own release, independent of
  release 2.

The optional rerank endpoint (§7.4) is in neither release's scope: it shipped in gateway v0.9.0 (thekim1/fors#68),
pulled forward from P2, and is no go-live gate (Decisions "Rerank pulled forward").

### 13.2 Migrations and rolling update

**Migrations**: release 1 `KeyResidencyCeiling`; release 2 `DepartmentMemberGroups`. Feature A's are the gateway's
integration phases'. **Rolling update: yes** for both, with these rules in the upgrade guide:

1. Set `RequireMaxResidency` on a key only after every gateway instance runs release 1: an old instance ignores
   `x-fors-max-residency` (callers must check `x-fors-residency`). The same holds for `x-fors-on-behalf-of` in the
   integration phase 2: an old instance ignores it and sends no echo, which is why Källa checks the echo (§5.2).
2. Do not rotate a key with `RequireMaxResidency` until the admin API is fully upgraded: an old instance's rotation
   does not copy the flag.
3. Release 2: during the window an old admin API instance signs users in without group-derived scope and without the
   tenant/guest checks; sessions are fixed at sign-in, so affected admins sign in again after the rollout.

### 13.3 Upgrade-guide outline

**Release 1.** *Rolling update: yes* (rules above). New request header `x-fors-max-residency` and error code
`max_residency_not_met`; `503` with `Retry-After` for unavailable in-ceiling targets; `/v1/models` honours the
ceiling; `x-fors-residency` now documented; key flag `RequireMaxResidency`; FakeLlm takes `dimensions` /
`fake-embed-<n>` (development only). Nothing changes for clients that send no ceiling.

**Release 2.** *Rolling update: yes* (rules above). *Check before upgrading*: guests are now refused at sign-in by
default (`acct = 1` or a foreign `idp`): set `FORS_OIDC_REFUSE_GUESTS=false` to keep admitting them; set
`FORS_OIDC_TENANT_ID` on Entra. *Opt in*: member groups on *Organisation* (the IdP must emit them in the group claim).
New `/bff/user` field, Data API `memberGroups`, team `code`, events 2008/3003. The integration phases have their own
upgrade-guide entries (gateway `docs/integrations.md`).

### 13.4 Källa afterwards

CI and tests run on the FakeGateway (`../platform/repo-and-deployment.md` §4.3), which implements the release-1
contract and the integration contract of §5 (built against the gateway's published OpenAPI document).

**P1a, before release 1** (Decisions "P1a plan answers", "Interim gateway for P1a"): Källa calls the gateway as it
is (v0.8.5), on an interim **test/dev gateway instance** on a fixed host (HTTPS, public certificate) with EU-hosted
embedding and chat models; it is the interim J10, and the same host's Keycloak holds the shared realm `fors` (J11).
Address and keys live only in user secrets or `deploy/.env` and secret files, never in the repository. Everything is
EU-hosted until on-prem hardware exists (register M2): the pilot embedding profile is `text-embedding-3-large` at 1024
dimensions, requested with `dimensions`. Källa already behaves as for release 1: every call carries
`x-fors-max-residency` with the level's key and the answer's `x-fors-residency` is checked before a byte is used
(`ResidencyCeilingHandler`); v0.8.5 ignores the request header, so until release 1 the ceiling rests on the keys'
`AllowedResidencies` and that check. `GET /v1/models` is not yet filtered by the ceiling.

**With release 1, in P1b before go-live (until the gateway's integration phase 1):**

- **Keys per level of the pilot department's team** (plan §4): ordinary team keys `OnPrem`-only (`AllowedResidencies = [OnPrem]`), `Eu`-max (`[OnPrem, Eu]`) and open
  (all), each issued per deployable that needs it with `AllowedIpRanges` of that deployable's hosts (`Entities.cs:89`)
  and `RequireMaxResidency = true`. The worker never holds the open key. Which deployable holds which level is the
  platform plan's (§6). Every Källa call is charged to the pilot team. The team's admins can edit these keys (no
  delegation, so not gateway-admin only); the explicit ceiling and the `x-fors-residency` check still hold whatever
  they change. The gateway applies the team's request policy to non-delegated requests, so the pilot team keeps none.
  The worker's keys also carry the gateway's existing `MaxConcurrentRequests` cap (429 + `Retry-After`), so a
  misconfigured worker cannot starve chat (processing design §11).
- Every call carries `x-fors-max-residency` (`OnPrem`, `Eu` or `External` for `public`) and the run headers (stored
  from the integration phase 2 on; older gateways ignore them); no `x-fors-on-behalf-of`. The answer's `x-fors-residency` is verified.
- The admin's sync reads departments, teams and member groups from `GET /v1/fors/context` with its credential key and
  the scope `organisation.read` (§5.1; B-35). No Data API key is used.
- Saving an assistant calls `GET /v1/models` with the level's key and `x-fors-max-residency` = the level's ceiling,
  and refuses a model that is not listed (§3.4, admin design §4.6 V8).
