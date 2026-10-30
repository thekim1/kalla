# Stream A brief: platform and the ingestion side (P0)

You build the parts of the P0 skeleton that run, ship and restore the platform, plus the worker's job machinery.
Stream B builds the application side in parallel. You start once Stage 0 is merged (`README.md` §1, hand-off H1).

## Read first

1. [`../../AGENTS.md`](../../AGENTS.md): working rules (owned paths, deviations, results, PRs).
2. [`README.md`](README.md): ownership, hand-offs and sync points.
3. [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md): all of it. Your items are §11 rows, and
   their done-when criteria there are binding.
4. Per item, the references in the table below. Read [`../simplification-pass.md`](../simplification-pass.md) §1, §3,
   §4 and §14 before any of them.

## Items, in order

| Order | §11 # | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|
| 1 | 5 | `images/postgres/Dockerfile` (digest-pinned stock pgvector + PGDG `pgbackrest`), `deploy/postgres/postgresql.conf`, `deploy/postgres/test.sh`, image job in CI → **hand-off H3** | repo-and-deployment §5.1–§5.2, data-model §14 | 127, 161, 122 |
| 2 | 13, 12 | Parser: `src/parser/` (FastAPI + Docling, Tesseract `swe`, models baked, supervisor with one slot, health and `/v1/info` with `build_hash`), `contracts/parser/v1/*.json`, `ContractTests`, parser CI job, `test_offline.py` without network | parser/design.md §7, §9–§13, §16, §19 | 57–59, 61–63, 65, 66, 72–74, 76, 78 (image size), 80 (memory), 109, 150 |
| 3 | 12 | `images/clamav/Dockerfile` (direct freshclam, no mirror); `scripts/Start-Dev.ps1`, `scripts/Start-E2E.ps1` (adapted from the gateway) | repo-and-deployment §4, §5.5 | 89 |
| 4 | 3 | `verify.yml` beyond Stage 0's backend job: `ui`, `deploy-checks`, `images` | repo-and-deployment §8 | 91 |
| 5 | 14 | `deploy/compose.prod.yaml`, `compose.ingestion.yaml` (networks, zones, limits, one slot, `DOTNET_GCHeapHardLimitPercent=0x4B` on every .NET service), `init-deployment.sh`, `deploy/host/` (slice, firewall, backup timer), `backup-postgres.sh`, `restore-pitr.sh`, `backup-uploads.sh`, `escrow-keys.sh`, `compose.openwebui.yaml` (optional pilot stack, §12 #8); restore drill locally first, then on the test host (**H6**) | repo-and-deployment §6, §7.4; performance-pass §3.1 | 46, 90, 124, 129, 134 |
| 6 | 20 | Worker dispatcher (**after H2**): `WorkerInstances` heartbeat, leases, reaper (reclaim ≠ attempt; poison suspect after 3 reclaims), `LISTEN rag_jobs`, the `command` kind applied → **hand-off H4** | design-engine.md §9, §16, §20; data-model §4 | – |
| 7 | 21 | Your scale seams: `IBlobStore` write-once contract (`IfAbsent`, tombstones) + file-system store, `IJobQueue` (Postgres), `ConnectionStrings__ragdb-listen`, `Worker:Classes`, `Worker:Concurrency:<class>` | performance-pass §8; data-model §9 | – |
| 8 | 17 | `infra/entra/` Bicep (Graph extension) + `entra.yml` (plan only on PR) for package 1; the user submits it | identity/design.md §12; repo-and-deployment §8.1 | 84 |
| 9 | 22 | Capacity on the real gateway hardware (weakness review §5): scripts in `scripts/capacity/` measuring BGE-M3 embedding throughput by batch size, query-embedding p95 under an ingestion load, and condense and answer model latency (p95, TTFT p99 for a 16k-token prompt); first against a local gateway, then on the real hardware (**H8**); numbers recorded against the estimates | performance-pass §2, §3.9; ingestion design-engine §18 | 98, 99, 104, 125, 149 |
| 10 | 19 | Docs: `local-development.md`, `production-setup.md`, `runbook.md` (restore, escrow), `upgrade-guide.md` (B writes `security-events.md`) | platform/plan.md §10; repo-and-deployment §6.2, §7.4 | – |
| 11 | 15 | `release.yml`, `build-release-bundle.sh`, tag `v0.1.0` (**integration sync point only**) | repo-and-deployment §7, §8; platform/plan.md §10 | – |
| 12 | 16 | Deploy `v0.1.0` to the test host behind the proxy, with the user | repo-and-deployment §6 | – |

If an item is blocked on a hand-off, start the next one and come back to it later.

## You provide, you consume

- **Provide:** H3 (Postgres image), H4 (worker applies `command`), the Entra Bicep for H7, and the parser contract
  that the worker's parser client uses in P1.
- **Consume:** H2 (B's migrations; the schema is B's, and table or column needs go to B as issues), H5 (service
  images and probes from Stage 0 and B), H6 (test host, backup target and escrow location from the user), H8 (access
  to the gateway hardware from the user).
- **AppHost** is B's. Stage 0 registers the parser and clamav resources. Ask for changes to them through an issue.

## Finished when

Every row above meets its §11 done-when, with evidence in its PR. Every listed `to-verify.md` row is recorded in
[`results-a.md`](results-a.md). The restore drill is documented with the achieved RPO and RTO. `v0.1.0` is green
through the proxy on the test host. The capacity numbers (#22) are recorded; a shortfall is reported to the main
session for register M2.
