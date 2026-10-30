# Handover

State of the Ume.Rag work for the next session. Updated 2026-10-30, end of session 3.
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's existing **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the
gateway and wants to do better than **Eneo** (`~/git/eneo`), which they find slow and a "kitchen sink" monolith.

This repo holds **design only**. The design is **complete**: ten detail steps, a consistency pass and a performance
pass, all reviewed and pushed. **Next is P0 implementation.**

## Reading order

1. `01-general-idea.md` §10 **Decisions table**: authoritative; later rows win over earlier ones. Sections 3–9 are
   the original idea; where they differ, the plans and the table win.
2. `plans/README.md`: plan folders, phases P0–P3, links to the registers.
3. `plans/architecture.md`: Mermaid diagrams (system overview, chat request, ingestion, agent tool call).
4. The detail documents for the area being worked on (map below); each ends with "Changes to other documents",
   answered questions and "To verify".

## Document map

| Area | First-level plan | Detail documents |
|---|---|---|
| Platform | `plans/platform/plan.md` (deployables, zones, residency matrix, level policies §8a) | `data-model.md` (schemas, roles and grants, Swedish FTS, indexes, job queue, outbox, tiers, encryption, retention, blobs, Redis); `repo-and-deployment.md` (repo layout, build, Aspire, FakeGateway, images, Compose, networks, zones, secrets, settings rule §3, CI, observability, Kubernetes later, **P0 checklist §11**) |
| Identity | `plans/identity/plan.md` | `design.md` (BFF copied from the gateway, feature C member groups, `RagPrincipal`, `IAccessPolicy`, Open WebUI JWT trust, keys, OAuth, MCP metadata, OBO P3, security events 5001–5015 / 6001–6007, Keycloak realm, Bicep package 1) |
| Gateway changes | `plans/gateway-changes/plan.md` | `design.md` (residency ceiling B, member groups C, delegation A; releases B → C → A; catalogue additions), `adr-drafts.md` (rows 018–020 for the gateway repo; gateway repo untouched) |
| Parser | `plans/parser/plan.md` | `design.md` (Docling sidecar, `/v1/inspect`, `/v1/ner`, legacy Office/ODF/email, OCR modes, supervisor, image) |
| Ingestion | `plans/ingestion-worker/plan.md` | `design-engine.md` (pipelines, Merkle hashes, jobs, scheduler, preview, publish/gating/rollback, index writer); `design-connectors.md` (URL list, crawl, shared frontier, egress guard, renderer, uploads, attachments, ClamAV, deletion guard, SharePoint P2); `design-processing.md` (auto-parse, chunking, PII + NER, embed, evaluation sets, gates) |
| Knowledge API | `plans/knowledge-api/plan.md` | `design.md` (`/v1/retrieve`, hybrid search in three statements, RRF in .NET, halfvec HNSW, coverage, sections, citations, download grants, OpenSearch seam) |
| Chat API | `plans/chat-api/plan.md` | `design.md` (OpenAI-compatible API, Open WebUI integration with `owui-tasks`, condense, prompt frame, residency checks before the first byte, citations, writer with key epochs, feedback import, retention, C1 attachments P2) |
| Tools (P2) | `plans/tools/plan.md` | `design.md` (tool hub `rag-tools`, skills per the Agent Skills spec, tool policy, clearance/result level, drift and two-person approval, agent loop, `/mcp`, .NET MCP server template, mock fee calculator) |
| Admin | `plans/admin/plan.md` | `design.md` (BFF + Swedish UI, `Ume.Rag.Answering` test panel, roles incl. `rag-reviewer`, write path, cross-rules V1–V32, pilot setup checklist, Vue Flow canvas P2, WCAG 2.2 AA) |
| Cross-cutting | – | `plans/consistency-pass.md` (what the pass changed, settings rule); `plans/performance-pass.md` (capacity model, bottlenecks, scale-out, triggers, scale test); `plans/postponed-decisions.md` (register); `plans/to-verify.md` (126 checks by topic and phase) |

Background: `02-architecture-review.md` (R1–R7, all applied), `platforms/` (Eneo, Dify, RAGFlow, Onyx, Open WebUI,
Kernel Memory, OpenSearch, Bifrost; synthesis `00-comparison.md`). Reference clones: `~/git/llmgateway`,
`~/git/eneo`, `~/git/rag-platforms/{dify,ragflow,onyx,open-webui,kernel-memory,bifrost}`.

## Key decisions (full list: Decisions table)

- **Services**: `Ume.Rag.Api` (roles chat, knowledge, mcp; shared `Ume.Rag.Answering` + `Ume.Rag.Retrieval`),
  `Ume.Rag.AdminApi` + Swedish admin UI, ingestion worker (`Worker:Classes` per instance), Python Docling parser
  sidecar (incl. OCR and Swedish NER), headless renderer, ClamAV, tool hub `rag-tools` (P2); own Postgres
  (pgvector + Swedish Hunspell) and Redis, separate from the gateway.
- **Gateway**: every model call goes through it (delegated platform key `x-ume-on-behalf-of`, residency ceiling
  `x-ume-max-residency`, interim per-team keys); embeddings always on-prem BGE-M3 (1024 dims).
- **Classification**: `public` / `internal` / `sensitive`; residency public any, internal EU or on-prem, sensitive
  on-prem; client registry with ceilings (Open WebUI `internal`, MCP clients `public` by default); follow-ups with
  client-sent history run at max(assistant level, client ceiling).
- **Identity**: the gateway's model (OIDC/BFF, `roles`/`departmentCodes`/`teamCodes`, `UME_RAG_OIDC_*`, `rag-*` roles
  on the gateway's admin registration); feature C added to both systems; no Entra registration needed before the
  pilot.
- **Data protection**: chat content and blobs envelope-encrypted at every level; monthly shred-bucket keys for
  crypto-shredding; file key provider in the pilot (Key Vault K1); 90-day chat history (A1 pending).
- **Pilot (P1)**: URL lists, website crawl (≤ 10 req/s per host, night window), uploads (50 MB), shared document
  store, flat ~400-token chunks with section expansion, evaluation sets with publish gates, RAG assistants
  (prompt + collections), Open WebUI for one department set up by its own admins, feedback incl. Open WebUI import,
  limited conversation review by `rag-reviewer`.
- **P2**: skills, agent assistants, tool hub, `/mcp`, hosted MCP servers, SharePoint, pipeline canvas, chat
  attachments, widget; scale-out over 3–5 hosts (Compose per host with roles, S3 object store, Open WebUI on its own
  VM).
- **Settings rule**: deployment knobs are `Section:Key`; run-time values editors or admins change are
  `Settings.<Section>.<Key>` in `config."Settings"`.

## How we work (user preferences)

- **Ask before each level of detail**, with the question tool: up to 4 questions per round, recommended option
  first. Record every answer in the relevant document ("Answered") and in the Decisions table.
- **Verify a premise before putting it in a question.** In step 10 a question claimed the gateway admin UI was
  Swedish; it is English, and the user had to answer again.
- **One subagent writes one document from a precise brief and does not commit.** The main session reads the whole
  document, checks claims against the gateway and real package source, fixes problems and asks the user only what
  they must decide. A second subagent then carries the "Changes to other documents" rows into the other files. The
  main session reviews that diff, commits and pushes.
- Wide passes can run several subagents in parallel when each owns separate files and reports cross-cluster issues
  instead of editing them (step 11). A Workflow needs the user's explicit opt-in.
- **Stay compatible with the gateway**: copy its conventions, files, claims, config keys, key format and
  security-event scheme; when something is missing, add it to both systems (`gateway-changes`).
- **Check library claims against real source**: download packages into the scratchpad (`pip download --no-deps`,
  NuGet flat container, npm tarball), extract, read only, never execute. This caught Docling's legacy Office support
  (step 5), MCP protocol 2026-07-28 without sessions (step 9) and MinIO being archived (performance pass).
- A formatter in the user's editor sometimes realigns markdown tables: **re-read a file before editing it**.
- Commit at the end of each reviewed step, then push to `origin main`. All files are committed, including
  `platforms/`.
- The global agent-memory instructions (`.agent/`) do not apply: this repo has no `.agent/` directory.

## Progress

| Step | Document | Commit |
|---|---|---|
| Research, general idea, review, first-level plans | `01-general-idea.md`, `02-architecture-review.md`, `platforms/`, `plans/*/plan.md` | `8160fcd` |
| 1. Data model | `plans/platform/data-model.md` | `d889cd9` |
| 2. Repo, local dev, deployment | `plans/platform/repo-and-deployment.md` | `320fc82`, `d329b8d` |
| Postponed-decisions register | `plans/postponed-decisions.md` | `d2fcff3` |
| 3. Identity | `plans/identity/design.md` | `ffdccaf` |
| 4. Gateway changes | `plans/gateway-changes/design.md`, `adr-drafts.md` | `11908e4` |
| 5. Parser | `plans/parser/design.md` | `5c9e414` |
| 6a–6c. Ingestion engine, connectors, processing | `plans/ingestion-worker/design-*.md` | `080d4bd`, `e2a99d9`, `e804c72` |
| 7. Knowledge API | `plans/knowledge-api/design.md` | `2fe838d` |
| 8. Chat API (C1 decided) | `plans/chat-api/design.md` | `00f2242` |
| 9. Tools | `plans/tools/design.md` | `e2b8dee` |
| 10. Admin API + UI | `plans/admin/design.md` | `ec29ea9` |
| 11. Consistency pass + to-verify register | `plans/consistency-pass.md`, `plans/to-verify.md` | `55b6113` |
| Architecture diagrams | `plans/architecture.md` | `74f8b61` |
| Performance pass | `plans/performance-pass.md` | `3b79baa` |
| **P0 implementation** | `plans/platform/repo-and-deployment.md` §11 | **next** |

## Open items

**Waiting on people** (`plans/postponed-decisions.md`):
- **A1** archive law: are chat logs allmänna handlingar, is a gallringsbeslut needed? **Pilot blocker**; owner still
  to be named.
- **N1** product name, before the pilot.
- **R1** self-hosted GitHub runner (IT security), before the first release performance test.
- **S2** object store server (municipal S3, else SeaweedFS; with IT), before the first multi-host deployment (P2).
- **U1** crawler about page and robots.txt check with the web editors, before the first production crawl.
- **P2** licences of models, OCR weights, LibreOffice, fonts and dictionaries, before the pilot.

**Measured or checked during implementation**: 126 rows in `plans/to-verify.md`, grouped by topic with the phase
they must be resolved by. Two proposals still need numbers: the `ner` job lease and attempts (data-model §4.2), and
whether tool arguments get their own validator or the JSON Schema validator (P2 start).

## Next step: P0 implementation

P0 builds the foundation: a skeleton that builds, deploys and is observable. The ordered checklist is in
`plans/platform/repo-and-deployment.md` §11. It covers:
- the repo skeleton, Aspire AppHost and ServiceDefaults;
- migrations with the config outbox;
- the Postgres image with pgvector 0.8.7 and Swedish Hunspell;
- resource zones, the Keycloak realm, gateway-style keys and FakeGateway;
- CI and approval package 1;
- the P0 seams from the performance pass: S3-safe `IBlobStore`, `IJobQueue`, pooler-safe database access,
  `Worker:Concurrency:<class>`.

Before starting:
1. Review the postponed-decisions register (phase-start rule) and the P0 rows of `plans/to-verify.md`.
2. Ask the user where the code lives (a new repo, or `src/` here), the order of the P0 items, and whether the gateway
   changes (releases B → C → A) start in parallel in the gateway repo.
3. Apply the 7-day package rule (V1) when pinning versions.
