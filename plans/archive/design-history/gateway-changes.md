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
