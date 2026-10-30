# Handover

Status of the Ume.Rag design work, for the next session. Updated 2026-10-30 (session 3, steps 7–11; branch `main`, remote `origin` = github.com/thekim1/ume-rag-platform).

## What this is

Design (no code yet) of **Ume.Rag**, a RAG / crawling / chatbot platform for Umeå kommun, built as small .NET 10
services on top of the existing **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway), by the
same team. The user (gateway owner) explicitly wants to improve on **Eneo** (`~/git/eneo`), which they consider slow
and a "kitchen sink" monolith.

## Where everything is

| Path | Contents |
|---|---|
| `01-general-idea.md` | The design idea. **§10 Decisions table is authoritative**; sections 3–9 are the original idea, where they differ the plans and the table win |
| `02-architecture-review.md` | Review items R1–R7, all adopted and applied (the plans refer to them as R1–R7) |
| `platforms/` | Analyses of Eneo, Dify, RAGFlow, Onyx, Open WebUI, Kernel Memory/.NET, OpenSearch, Bifrost; `00-comparison.md` is the synthesis |
| `plans/README.md` | Index of plan folders and phases P0–P3 |
| `plans/*/plan.md` | First-level plan per area: `platform`, `identity`, `knowledge-api`, `chat-api`, `tools`, `ingestion-worker`, `parser`, `admin`, `gateway-changes`. Each ends with answered and open questions |
| `plans/platform/data-model.md` | **Detail step 1** (done): schemas, roles, every table, Swedish FTS, hybrid SQL, queue, outbox, tiers, encryption, retention, blobs, migrations |
| `plans/identity/design.md` | **Detail step 3** (done): BFF copied from the gateway, feature C, `RagPrincipal`, `IAccessPolicy` (13 ordered steps), Open WebUI JWT trust, keys, OAuth, MCP metadata, OBO (P3), security events 5001–6007, Keycloak realm, Bicep package 1, tests |
| `plans/platform/repo-and-deployment.md` | **Detail step 2** (done): repo layout, build, roles, Aspire/Keycloak/FakeGateway, images, compose and resource zones, release, backup, GitHub Actions, observability, P0 checklist |
| `plans/gateway-changes/design.md`, `adr-drafts.md` | **Detail step 4** (done): residency ceiling (B), member groups + tenant/guest refusal (C), delegation (A), FakeLlm dimensions, run headers, tool tests; releases B → C → A; ADR rows 018–020 to copy into the gateway repo |
| `plans/parser/design.md` | **Detail step 5** (done): routing worker vs parser, `/v1/inspect`, Docling native legacy/ODF/email backends behind a sandboxed `soffice` wrapper, attachment splitter → child documents, multipart contract, stored DoclingDocument, page ranges + stitching, OCR modes and bake-off, supervisor process model, security, image, tests. Docling claims checked against `docling-slim` 2.145.0 |
| `plans/ingestion-worker/design-engine.md` | **Detail step 6a** (done): engine model, definition format + validator + Merkle hashes (`Ume.Rag.Pipelines`), node interface and P1 catalogue, execution with segments and fence, cache, jobs/fairness/scheduler, runs, preview, publish/rollback, templates, index writer, config mid-run, observability, capacity, tests |
| `plans/ingestion-worker/design-connectors.md` | **Detail step 6b** (done): connector contracts and checkpoints, URL identity, URL list, crawl (robots, sitemaps, scope, traps, traversals), shared frontier with claims and releaser, egress guard, renderer fed by worker fetches, variants, uploads, attachments, ClamAV, change detection and effect tags, deletion guard with reconcile phase, SharePoint P2 outline |
| `plans/ingestion-worker/design-processing.md` | **Detail step 6c** (done): auto-parse rules and ranges, stitching, clean, split, chunk (tokenizer), metadata, quality check, PII detection + parser `/v1/ner`, embed, noise re-key, evaluation sets, gate, go-live floor |
| `plans/knowledge-api/design.md` | **Detail step 7** (done): `Ume.Rag.Retrieval` engine shared with the evaluator and test panel, `IRetriever` for the chat role, `/v1/retrieve` (problem details), keyword statement parallel to the query embedding then vector then one hydrate batch, RRF in .NET, exact scan ≤ 10,000 chunks, degrade to keyword-only, per-collection coverage, dedup/variants/cap 3, section expansion from neighbouring chunks (`OverlapChars`), citations with 7-day signed download grants, no result cache, OpenSearch seam, MCP outline. pgvector 0.8.7, `Pgvector` 0.3.2 and MCP SDK 2.3.0 checked against package source |
| `plans/chat-api/design.md` | **Detail step 8** (done): OpenAI-compatible contract, client-sent history authoritative (0 SQL on the hot path), Open WebUI with one connection and our `owui-tasks` task model, condense on follow-ups (on-prem), token budget, untrusted-source prompt frame, residency + echo checks before the first byte (follow-ups with history at max(level, client ceiling)), `[n]` renumbered by first citation, `source` events vs `url_citation`, output filter, fixed not-found/degraded texts, signed conversation ids, background writer with 7-day key epochs, Open WebUI feedback import and pruning job, C1 attachments (P2, chat-scoped). Open WebUI claims checked against v0.12.5 source |
| `plans/tools/design.md` | **Detail step 9** (done, all P2): tool hub `rag-tools` as the only credential holder (mTLS + service key), skills in `config` per the Agent Skills spec (no scripts, SemVer + served pointer, MAJOR auto-pin across departments, gate = retrieval + routing), tool servers with clearance + result level (agent run level fixed at start), deny-by-default policy with one list/call function, pinned definitions + 15-min drift + two-person re-approval, agent loop limits and cost estimate, stateless `/mcp` with per-skill endpoints, .NET template + OpenAPI code generator, mock `ume-mcp-avgifter`, events 5012–5015. MCP SDK 2.3.0 (protocol 2026-07-28, no sessions) and Agent Skills spec checked |
| `plans/admin/design.md` | **Detail step 10** (done): gateway BFF/CSP/problem details copied; `Ume.Rag.Answering` library so the test panel runs the real chat path in process; Swedish UI (the gateway's UI is English: deliberate divergence) with error codes mapped to Swedish; `rag-reviewer` role; `xmin` concurrency; one-transaction write path (change + outbox + audit + jobs); cross-rules V1–V31; pipeline `gating` state; `GatewayModels` sync in P1; pilot setup checklist; Vue Flow 1.48.2 canvas (P2) with keyboard list editor; WCAG 2.2 AA + one NVDA pass before the pilot |
| `plans/consistency-pass.md`, `plans/to-verify.md` | **Step 11** (done): what the consistency pass changed, the settings rule, decisions; the merged to-verify register (113 items by topic and phase) |
| `plans/performance-pass.md`, `plans/architecture.md` | **Performance pass** (done, after step 11): capacity model for the pilot and 10× (≈ 100k pages/h checked, 5–10M chunks, 3–5 hosts), binding resources (politeness, on-prem BGE-M3, NER lane, HNSW memory), Compose per host with roles, `IJobQueue` seam, lease leaders, Redis crawl leases, S3-safe `IBlobStore` (municipal S3 else SeaweedFS, S2), halfvec HNSW from P1, numeric scale triggers, scale test; Mermaid architecture diagrams |
| `plans/postponed-decisions.md` | **Register of postponed decisions** (K1 key service, T1 sensitive tier, A1 archive law, R1 runner, ...), with stopgap, deadline, owner. Review at each phase start; add new postponements here |

Cloned reference repos: `~/git/llmgateway`, `~/git/eneo`, `~/git/rag-platforms/{dify,ragflow,onyx,open-webui,kernel-memory,bifrost}`.

## How we work (user preferences)

- **Ask questions in rounds before each level of detail**, using the question tool (up to 4 at a time, a
  recommended option first). The user answers briefly; record every answer in the relevant plan ("Answered" list) and
  in the `01-general-idea.md` Decisions table.
- **Detail round = one subagent per step, sequentially.** The main session writes a precise brief, the subagent
  writes one document and does not commit, the main session **reviews** (reads the whole document, checks claims
  against the gateway code, fixes problems, carries accepted findings into other plans, asks the user what only they
  can decide), then **commits**. One step at a time.
- **Be as compatible with the gateway as possible**: copy its conventions, files, claims, config keys, key format,
  security-event scheme; when something is missing, add it to both systems (see `gateway-changes`).
- Commit only when asked or at the end of a reviewed step; the repo is `~/research/rag` (branch `main`, pushed to `origin`).
- A formatter in the user's editor sometimes realigns markdown tables: **re-read a file before editing it**.
- **Check library claims against real source**: when a subagent cannot find a library locally, download the package
  into the scratchpad (`pip download --no-deps`, extract, read only) and verify. In step 5 this showed Docling 2.145
  already reads legacy Office, ODF and email, which the draft had assumed it did not.

## Progress

| Step | Document | State | Commit |
|---|---|---|---|
| Research, general idea, review, first-level plans | – | done | `8160fcd` |
| 1. Platform data model | `plans/platform/data-model.md` | done, reviewed | `d889cd9` |
| 2. Platform repo, local dev, deployment | `plans/platform/repo-and-deployment.md` | done, reviewed | `320fc82`, `d329b8d` |
| Postpone Key Vault, postponed-decisions register | `plans/postponed-decisions.md` | done | `d2fcff3` |
| 3. Identity detail | `plans/identity/design.md` | done, reviewed | `ffdccaf` |
| 4. Gateway changes | `plans/gateway-changes/design.md`, `adr-drafts.md` (drafts; gateway repo untouched) | done, reviewed | `11908e4` |
| 5. Parser | `plans/parser/design.md` | done, reviewed | `5c9e414` |
| 6a. Ingestion engine | `plans/ingestion-worker/design-engine.md` | done, reviewed | `080d4bd` |
| 6b. Connectors | `plans/ingestion-worker/design-connectors.md` | done, reviewed | `e2a99d9` |
| 6c. Processing | `plans/ingestion-worker/design-processing.md` | done, reviewed | `e804c72` |
| 7. Knowledge API | `plans/knowledge-api/design.md` | done, reviewed | `2fe838d` |
| 8. Chat API (C1 decided) | `plans/chat-api/design.md` | done, reviewed | `00f2242` |
| 9. Tools (skills, tool hub, MCP server, template) | `plans/tools/design.md` | done, reviewed | `e2b8dee` |
| 10. Admin API + UI | `plans/admin/design.md` | done, reviewed | `ec29ea9` |
| 11. Consistency pass | `plans/consistency-pass.md` | done | see git log |
| **P0 implementation** | `plans/platform/repo-and-deployment.md` §11 | **next** | – |

After step 10: a consistency pass across all detail documents, then P0 implementation (P0 checklist in
`repo-and-deployment.md` §11).

## Key decisions to keep in mind (full list: Decisions table)

- Services: Knowledge API + Chat API + MCP (one binary `Ume.Rag.Api`, roles), tool hub (P2), ingestion worker,
  Python Docling parser sidecar, admin API/UI; Postgres + pgvector + Swedish Hunspell (own image, own instance,
  separate from the gateway), own Redis; GitHub Actions; Bicep for Entra IaC.
- All model calls through the gateway (delegated platform key `x-ume-on-behalf-of`, residency ceiling
  `x-ume-max-residency`; interim per-team keys). Residency: public any, internal EU/on-prem, sensitive on-prem.
- Three classification levels; client registry with ceilings (Open WebUI and MCP clients capped); embeddings always
  on-prem (BGE-M3, 1024 dims).
- Identity identical to the gateway's model (OIDC/BFF, `roles`/`departmentCodes`/`teamCodes`, `UME_RAG_OIDC_*`,
  `rag-*` roles on the gateway's existing admin registration with its own credential); department scope from member
  groups = gateway feature C, added to both systems. No Entra registration needed before the pilot.
- Chat content and blobs envelope-encrypted at every level; monthly shred-bucket master keys for crypto-shredding;
  **file key provider in the pilot** (Key Vault postponed, K1).
- Pilot: URL lists, crawl (with headless renderer), uploads (50 MB, ClamAV direct updates), Open WebUI for one
  department, RAG assistants (prompt + collections); skills, tools and MCP in P2.

## Open with the user

- R1 self-hosted runner (IT security), A1 archive law (owner to be named), N1 product name: see the register.
- Nothing else is waiting on the user right now.

## Next step in detail: P0 implementation

The design is complete and consistent (`plans/consistency-pass.md`). Next is P0 (foundation): the checklist in
`plans/platform/repo-and-deployment.md` §11 (repo skeleton, Aspire AppHost, ServiceDefaults, migrations with config
outbox, Postgres image with pgvector 0.8.7 + Swedish Hunspell, resource zones, Keycloak realm, gateway-style keys,
FakeGateway, CI, approval package 1). Before starting: review `plans/postponed-decisions.md` (phase-start rule) and the
P0 rows of `plans/to-verify.md`; ask the user about the implementation repo (this repo holds design only) and the
order of P0 items. Open with the user: R1 runner, A1 archive law (pilot blocker), N1 product name.
