# Progress: stream B (application and the serving side, P0)

Owner: stream B. Brief: [`stream-b.md`](stream-b.md). Rules: [`../../AGENTS.md`](../../AGENTS.md). Checklist and binding
done-when criteria: [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §11.

**How to use:** work top to bottom; item 1 first, because stream A waits on it. Take the first unticked step whose
hand-off has landed; if blocked, take the next item and come back. Tick each box in the PR that does the work and put
the PR number after it (`[x] … (#12)`). Record every `to-verify` row in [`results-b.md`](results-b.md) before ticking it here.

## Waiting on (tick when it lands)
- [x] **H1** Stage 0 merged ([`progress-stage-0.md`](progress-stage-0.md))
- [x] **H3** A's `ume-rag/postgres` image in CI (then switch `TestPostgres` to it) (A's #5: CI sets `UME_RAG_TEST_POSTGRES_IMAGE`, issue #6)
- [x] **H4** worker applies the `command` kind (then wire the admin enqueue) (#28)

## 1. Schema and roles (#6, first)
Refs: [data-model](../platform/data-model.md) §1, §2, §4, §5, §10, §14; [knowledge-api/design.md](../knowledge-api/design.md) §6, §15, §17
- [x] `deploy/postgres/init.sql`, `app-roles.sql`, `pg_hba.conf` (roles and grants of data-model §1)
- [x] `Ume.Rag.MigrationService`: schemas, config outbox, unpartitioned `Jobs` with the partial unique `DedupKey` index, `WorkerInstances`
- [x] Day-partitioned `RunItems`, `rag_partition_maintain()`
- [x] Role defaults `hnsw.ef_search`/`iterative_scan`/`max_scan_tuples`; `content.rag_or_query`
- [x] **Hand-off H2:** migrations merged; issue to stream A
- [x] `DatabaseRoleTests`; connection caps and `statement_timeout` asserted
- [ ] Pool sizes measured (P0 part recorded under to-verify #140: the connection budget holds; pool waits under load need the P1 chat path)
- [x] to-verify #2, #13, #18, #140, #142 recorded

## 2. Swedish decompounder (#5, .NET part)
Refs: [simplification-pass §3](../simplification-pass.md), [data-model §3, §14](../platform/data-model.md)
- [x] `SwedishDecompounder` in `Ume.Rag.Retrieval` with the `sv_SE` word list; unit tests (`bygglovsansökan` → `bygglov`, `ansökan`)
- [x] `SwedishTextSearchTests` (a part matches through the built-in `swedish` configuration)
- [x] Comparison against Postgres `ispell` recorded (to-verify #128)

## 3. Config-version library (#7)
Refs: [repo-and-deployment §4](../platform/repo-and-deployment.md), [data-model §5](../platform/data-model.md)
- [x] `ConfigVersionMonitor`: LISTEN `rag_config` + 2 s poll, `Config:MaxUnconfirmedSeconds`; log scope
- [x] Serialised config writes (`FOR UPDATE` on the version row)
- [x] **Done:** tests for revoked-key refusal on notification, 503 after the window, readiness green within it, two concurrent saves cannot both pass a cross-rule (`ConfigVersionTests`; the revocation runs against a stand-in key table until `config."Keys"` lands with #10)

## 4. Keys (#10)
Refs: [identity/design.md §6](../identity/design.md)
- [x] `ume-rag-sk-` keys, HMAC + pepper, prefix, rotation grace in `config."Keys"`
- [x] **Done:** the gateway's `KeyTests` copied and passing

## 5. FakeGateway and gateway client (#8)
Refs: [repo-and-deployment §4.3](../platform/repo-and-deployment.md), [gateway-changes/design.md](../gateway-changes/design.md) §3, §6 (release 1 only, [weakness-review §5](../weakness-review.md))
- [x] `Ume.Rag.FakeGateway`: 1024-dim embeddings, residency headers, per-level dev keys, catalogue
- [x] Modes: 400 without a ceiling for `RequireMaxResidency` keys, `403 max_residency_not_met`, 503 with `Retry-After`; `/v1/models` filtered by the ceiling; rerank fake
- [x] Gateway client `DelegatingHandler` refusing a call without an explicit ceiling or with another level's key
- [x] **Done:** API tests use it; a wrong-residency answer is aborted; the handler test covers every call site (no service calls the gateway yet; every gateway client is registered through `AddGatewayClient`, which the handler tests exercise)

## 6. Identity (#9)
Refs: [identity/design.md](../identity/design.md) §1–§3, §10, §11; [identity/plan.md §3](../identity/plan.md) (P1 department scope from hand-entered member groups)
- [x] `dev/keycloak/ume-rag-realm.json` incl. `rag-reviewer` (#35)
- [x] OIDC/BFF and claim mapping copied from the gateway admin API (`UME_RAG_OIDC_*`); audit actor `oid` (#32)
- [x] Postgres admin session store; `AdminSessionTests` copied (to-verify #163)
- [x] `OidcClaimMappingTests` copied; member-group resolver tested against FakeGateway data (#32)
- [ ] to-verify #83, #151 recorded
- [x] **Done:** sign-in to the empty admin UI as each test user shows the right roles and departments (incl. one from a group claim) (user test round 2026-10-31, `rag-group-member` via Byggnadsnämnden)
- [x] **Hand-off H7:** realm and claim mapping ready for the user's package 1 (#35)

## 7. Admin skeleton and write path (#11)
Refs: [admin/design.md](../admin/design.md) §2–§4, §11, §12
- [x] Admin UI skeleton: copied design system, Swedish `labels.ts`/`messages.ts`, Vite on 5174, `design-system.lock` (#45)
- [x] `AdminContext` write path: change + outbox + audit in one transaction (#38)
- [x] `PermissionMatrix` role tests (#38)
- [x] Org sync against the FakeGateway catalogue (#39)
- [x] `contracts/admin/v1/error-codes.json` export (#38)
- [x] **Done:** lint/typecheck/test/build green; a saved change bumps the config version with one audit entry; synced departments visible (`verify.yml` admin-ui job, `WritePathTests`; user test round 2026-10-31)
- [ ] `command` enqueue from the admin (after H4)

## 8. Scale seams, your part (#21)
Refs: [performance-pass §8](../performance-pass.md)
- [ ] Pooler-safe SQL rules + scan test over all SQL (no forbidden session-state SQL)

## 9. Docs (#19, your part)
- [ ] `docs/security-events.md` ([identity/design.md §10](../identity/design.md))

## 10. Follow-ups
- [x] `TestPostgres` switched to `ume-rag/postgres` (after H3) (A's #5 sets the image in CI; nothing to change in TestKit)
- [ ] Integration support: issues from the main session's review answered before `v0.1.0`
