# Consistency pass (detail step 11, 2026-10-30)

> **Folded into the detail documents on 2026-11-01. Not binding; kept for the reasoning.** The ledger at the end lists each item and where it went.

After the ten detail steps, every document was checked against the others and against the Decisions table
(`../../../01-general-idea.md` §10). Three cluster passes ran in parallel, each editing only its own files, then one
cross-cluster pass applied the remaining fixes and checked shared names. No design decision was reopened; where two
documents disagreed, the later step or the Decisions table won.

| Pass | Files | Fixes |
|---|---|---|
| A: platform, identity, gateway | `platform/*`, `identity/*`, `gateway-changes/*`, `../../postponed-decisions.md`, `../../README.md`, `01-general-idea.md` | ≈ 70 |
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
- Package, behaviour and performance checks: [`../../to-verify.md`](../../to-verify.md) (113 items by topic and phase).


## Ledger (2026-11-01)

Every item of this document, in document order, and where it went. Kinds: *rule* (stated in the detail document named), *risk* (kept in a register), *rationale* (binds nothing), *reference* (stays here). Statuses: *folded* (written into the target), *already there* (the target already stated it), *obsolete* (superseded; the Decisions row or "as built" is named), *not folded* (reason given).

| ID | Pass § | Item | Kind | Status | Target |
|---|---|---|---|---|---|
| C-1 | intro ¶1 | Method: three cluster passes in parallel, then one cross-cluster pass; no decision reopened; later step or Decisions table won | rationale | rationale: binds nothing | – |
| C-2 | intro table | Fix counts per cluster (A ≈ 70, B ≈ 75, C 34, cross-cluster 15 of 26 + 5 name fixes + 2 decisions) | rationale | rationale: binds nothing | – |
| C-3 | Main corrections ¶1 | Decisions table: missing rows for steps 1, 2, 4 added; superseded parts marked (feedback comments read by `rag-reviewer` and `rag-departmen… | rationale | rationale: binds nothing | – |
| C-4 | Main corrections ¶2 | `ner` job class (6c) added to engine, data-model §4.2, ingestion/parser plans, parser design (lease and attempts proposed) | rule | obsolete: Simplification pass (engine §7.4); obsolete: as built (class dropped) (data-model §4.2) | data-model §4.2, engine §7.4 |
| C-5 | Main corrections ¶2 | `attachment` (P2) added to the `Jobs` class CHECK | rule | already there | data-model §4.1; §4.2, engine §9.1 |
| C-6 | Main corrections ¶3 | Ingestion and parser plans brought up to date with their designs (6b/6c, parent-child chunks, cache key, OCR default, `gating`, upload batc… | rationale | rationale: binds nothing | – |
| C-7 | Main corrections ¶4 | `Ume.Rag.Answering` holds the knowledge core, the chat answer core and the P2 agent loop, in every document | rule | already there | chat §1, repo-and-deployment §1, §3 |
| C-8 | Main corrections ¶5 | `GatewayModels` is P1, synced by the admin's `GatewayModelSync`, with `Kind` + `Residencies[]` | rule | already there | admin §4.10, data-model §2.1 |
| C-9 | Main corrections ¶5 | Admin rule V32: evaluation, judge, condense and skill-gate models must be on-prem `GatewayModels` rows | rule | folded | admin §4.6 |
| C-10 | Main corrections ¶6 | Tool-server levels: clearance + result level and a fixed agent run level replace "≤ assistant level" / "level rises" | rule | already there | tools §1 row 12, §5.3 |
| C-11 | Main corrections ¶7 | Master keys: chat keys only in `rag-api`/`rag-admin`; blob keys in `rag-worker`/`rag-admin`, read-only in `rag-api` | rule | already there | platform-plan §8a, repo-and-deployment §6.3 |
| C-12 | Main corrections ¶8 | Parser image ≤ 4.0 GB with the NER model | rule | folded | parser §12, repo-and-deployment §5.3 |
| C-13 | Main corrections ¶8 | Corrected memory sums | rationale | rationale: binds nothing | – |
| C-14 | Main corrections ¶8 | ≈ 10 missing settings keys and the secrets `ToolHub__ServiceKey`, `Api__ServiceKey` (P2) added | rule | not folded: obsolete with in-process hub (no ToolHub__ServiceKey; Api__ServiceKey P2 already in §6.3) | repo-and-deployment §6.3 |
| C-15 | Main corrections ¶8 | `rag-reviewer` in the Keycloak realm and approval package 1 | rule | already there | identity §1.3, §11, §12.1 |
| C-16 | Main corrections ¶8 | P0 checklist gained the admin and worker items | rationale | rationale: binds nothing | – |
| C-17 | Main corrections ¶9 | Names checked across documents: Redis keys, security events (5001–5015, 5101–5199, 6001–6007), metrics, job classes, roles, tables, secrets… | rationale | n/a (rationale) | identity §10 |
| C-18 | Settings rule | Deployment/infrastructure knobs (timeouts, pools, concurrency, endpoint limits, URLs, modes, secrets) are `Section:Key` (env `Section__Key`… | rule | already there | repo-and-deployment §3 |
| C-19 | Decided ¶1 | P2 host memory: 24 GB pilot host keeps both `b` slots; Open WebUI moves to its own VM in P2 | rule | already there | repo-and-deployment §6.2 |
| C-20 | Decided ¶2 | Download-grant lifetime is a deployment setting only (`Knowledge:DownloadGrantLifetime`) | rule | already there | knowledge §12.1, knowledge §4.6 |
| C-21 | Decided ¶2 | Identical chunk text in different documents is not collapsed in P1 | rule | already there | knowledge §8 |
| C-22 | Still open ¶1 | `ner` lease (2 min) and attempts (3) are proposals until measured | risk | dropped: no `ner` class exists | – |
| C-23 | Still open ¶2 | Tool-argument validation: in-house subset or the P1 JSON Schema validator, decided at P2 start | risk | n/a (risk) | tools §4.7 |
| C-24 | Still open ¶3 | Package, behaviour and performance checks are in `../../to-verify.md` (113 items) | rationale | rationale: binds nothing | – |
