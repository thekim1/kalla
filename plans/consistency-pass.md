# Consistency pass (detail step 11, 2026-10-30)

After the ten detail steps, every document was checked against the others and against the Decisions table
(`../01-general-idea.md` §10). Three cluster passes ran in parallel, each editing only its own files, then one
cross-cluster pass applied the remaining fixes and checked shared names. No design decision was reopened; where two
documents disagreed, the later step or the Decisions table won.

| Pass | Files | Fixes |
|---|---|---|
| A: platform, identity, gateway | `platform/*`, `identity/*`, `gateway-changes/*`, `postponed-decisions.md`, `README.md`, `01-general-idea.md` | ≈ 70 |
| B: ingestion, parser | `ingestion-worker/*`, `parser/*` | ≈ 75 |
| C: data plane, admin | `knowledge-api/*`, `chat-api/*`, `tools/*`, `admin/*` | 34 |
| Cross-cluster | any | 15 of 26 reported issues still open and fixed (11 already fixed by the owning cluster), 5 name fixes, 2 recorded decisions |

## Main corrections

- **Decisions table**: the missing rows for steps 1, 2 and 4 added; superseded parts marked (feedback comments are read
  by `rag-reviewer` and `rag-department-admin`, not "editors"; query side refined by steps 7–8).
- **`ner` job class** (6c) was missing from the engine, data-model §4.2, the ingestion and parser plans and the parser
  design; added everywhere (lease and attempts *proposed*); `attachment` (P2) added to the `Jobs` class CHECK.
- **Ingestion and parser plans** were behind their designs (6b/6c "to follow", parent-child chunks, old cache key, old
  OCR default, no `gating`, no upload batches); brought up to date.
- **`Ume.Rag.Answering`** (admin step) now consistently holds the knowledge core, the chat answer core and the P2
  agent loop in every document.
- **`GatewayModels`**: P1, synced by the admin's `GatewayModelSync`, `Kind` + `Residencies[]`, everywhere; new admin
  rule V32: evaluation, judge, condense and skill-gate models must be on-prem `GatewayModels` rows.
- **Tool-server levels**: clearance + result level and the fixed agent run level replace every "≤ assistant level" and
  "level rises" wording.
- **Master keys**: chat keys only in `rag-api`/`rag-admin`; blob keys in `rag-worker`/`rag-admin`, read-only in
  `rag-api` (the worker encrypts blobs, which the old text forgot).
- **Deployment**: parser image ≤ 4.0 GB with the NER model; corrected memory sums; ≈ 10 missing settings keys and the
  `ToolHub__ServiceKey`, `Api__ServiceKey` (P2) secrets; `rag-reviewer` in the Keycloak realm and approval package 1;
  P0 checklist gained the admin and worker items.
- **Names**: Redis keys, security events (5001–5015, 5101–5199, 6001–6007), metrics, job classes and kinds, roles and
  policies, table/column names, secrets and `Ume.Rag.*` projects checked across all documents.

## Settings rule (repo-and-deployment §3)

Deployment and infrastructure knobs (timeouts, pool sizes, per-process concurrency, endpoint-protection limits, URLs,
modes, secrets) are `Section:Key` settings (env/secret file `Section__Key`). Values editors or platform admins change at
run time (models, thresholds, product quotas, allow-lists, defaults, caps) are `Settings.<Section>.<Key>` in
`config."Settings"`. Each value lives in one place.

## Decided in the pass

- **P2 host memory** (user, 2026-10-30): the 24 GB pilot host keeps both `b` slots; Open WebUI moves to its own VM in P2.
- Download-grant lifetime is a deployment setting only (`Knowledge:DownloadGrantLifetime`); identical chunk text in
  different documents is not collapsed in P1 (knowledge §8).

## Still open (none blocking)

- `ner` lease (2 min) and attempts (3) are proposals until measured (data-model §4.2).
- Tool-argument validation: in-house subset or the P1 JSON Schema validator, decided at P2 start.
- Package, behaviour and performance checks: [`to-verify.md`](to-verify.md) (113 items by topic and phase).
