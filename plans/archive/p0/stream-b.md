# Stream B brief: application and the serving side (P0)

You build the application parts of the P0 skeleton: schema, config library, keys, identity, FakeGateway and the
admin skeleton with its write path. Stream A builds the platform side in parallel. You start once Stage 0 is merged
(`README.md` §1, hand-off H1).

## Read first

1. [`../../../AGENTS.md`](../../../AGENTS.md): working rules (owned paths, deviations, results, PRs).
2. [`README.md`](README.md): ownership, hand-offs and sync points.
3. [`../../platform/repo-and-deployment.md`](../../platform/repo-and-deployment.md) §1–§4 and §11. Your items are §11 rows,
   and their done-when criteria there are binding.
4. [`../../platform/data-model.md`](../../platform/data-model.md): all of it. You own the schema.
5. Per item, the references in the table below. Read [`../passes/simplification-pass.md`](../passes/simplification-pass.md) §1, §3,
   §6, §7 and §14 before any of them.

## Items, in order

| Order | §11 # | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|
| 1 | 6 | **First, because stream A waits on it (H2).** `deploy/postgres/init.sql`, `app-roles.sql`, `pg_hba.conf`; `Ume.Rag.MigrationService` with schemas, config outbox, unpartitioned `Jobs` with the partial unique `DedupKey` index, `WorkerInstances`, day-partitioned `RunItems`, `rag_partition_maintain()`, role defaults `hnsw.*`, `content.rag_or_query`; `DatabaseRoleTests`, connection caps and `statement_timeout` asserted; pool sizes measured | data-model §1, §2, §4, §5, §10, §14; knowledge-api/design.md §6, §15, §17 | 2, 13, 18, 140, 142 |
| 2 | 5 | `SwedishDecompounder` in `Ume.Rag.Retrieval` with the `sv_SE` word list and unit tests (`bygglovsansökan` → `bygglov`, `ansökan`); `SwedishTextSearchTests`; comparison against Postgres `ispell` recorded | simplification-pass §3; data-model §3, §14 | 128 |
| 3 | 7 | Config-version library (`ConfigVersionMonitor`: LISTEN `rag_config` + 2 s poll, `Config:MaxUnconfirmedSeconds`), log scope, serialised config writes (`FOR UPDATE` on the version row) | repo-and-deployment §4; data-model §5 | – |
| 4 | 10 | Keys in gateway style (`ume-rag-sk-`, HMAC + pepper, prefix, rotation grace) in `config."Keys"`; the gateway's `KeyTests` copied | identity/design.md §6 | – |
| 5 | 8 | `Ume.Rag.FakeGateway` (1024-dim embeddings, residency headers, per-level dev keys, 400/403/503 modes, `/v1/models` filtered by the ceiling, rerank fake, catalogue); gateway client `DelegatingHandler` that refuses a call without an explicit ceiling or with another level's key | repo-and-deployment §4.3; gateway-changes/design.md §3, §6 | – |
| 6 | 9 | `dev/keycloak/ume-realm.json` (incl. `rag-reviewer`); OIDC/BFF and claim mapping copied from the gateway admin API; Postgres admin session store; `OidcClaimMappingTests`, `AdminSessionTests` copied; audit actor `oid` | identity/design.md §1–§3, §10, §11; admin/design.md §12 | 83, 151, 163 |
| 7 | 11 | Admin UI skeleton (copied design system, Swedish `labels.ts`/`messages.ts`, Vite 5174, `design-system.lock`); admin write path (`AdminContext`: change + outbox + audit in one transaction); `PermissionMatrix` role tests; org sync against the FakeGateway catalogue; `contracts/admin/v1/error-codes.json`; then the `command` enqueue once **H4** lands | admin/design.md §2–§4, §11, §12 | – |
| 8 | 21 | Your scale seams: pooler-safe SQL rules + scan test over all SQL | performance-pass §8 | – |
| 9 | 19 | `docs/security-events.md` | identity/design.md §10 | – |

If an item is blocked on a hand-off, start the next one and come back to it later.

## You provide, you consume

- **Provide:** H2 (migrations; announce it in an issue labelled `stream-a` when merged), H5 (service images build and
  every probe answers), the realm and claim mapping for H7, and the schema itself. A's table and column requests
  arrive as issues labelled `stream-b`. Answer each one with a migration.
- **Consume:** H3 (A's `ume-rag/postgres` image; then switch `TestPostgres` to it in one PR), H4 (worker applies
  `command`).
- **AppHost and TestKit** are yours. A asks for changes to them through issues.

## Finished when

Every row above meets its §11 done-when, with evidence in its PR. Every listed `to-verify.md` row is recorded in
[`results-b.md`](results-b.md). Signing in to the empty admin UI as each Keycloak test user shows the right roles and
departments. A saved change bumps the config version and writes one audit entry.
