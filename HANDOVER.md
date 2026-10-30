# Handover

State of the Ume.Rag work for the next session. Updated 2026-10-30, end of session 4.
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's existing **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the
gateway and wants to do better than **Eneo** (`~/git/eneo`), which they find slow and a "kitchen sink" monolith.

This repo holds **design only**. The design is **complete**: ten detail steps, a consistency pass, a performance pass
and (session 4) a **simplification pass**, all reviewed and pushed. **Next is P0 implementation.**

Plain-language overview for stakeholders (private artifact, share from its menu):
https://claude.ai/artifact/GzKzra4RwKdf7xEWFc4EAM

## Reading order

1. `01-general-idea.md` §10 **Decisions table**: authoritative; later rows win over earlier ones. The last two rows
   (simplification pass, site profiles) override much of what comes before. Sections 3–9 are the original idea.
2. `plans/simplification-pass.md`: the ten review changes in decided form, §11a conflicts settled during the
   rewrite, §13 umea.se findings and learned site profiles. Read this before any detail document.
3. `plans/README.md`: plan folders, phases P0–P3, links to the registers and passes.
4. `plans/architecture.md`: Mermaid diagrams (system overview, chat request, ingestion, agent tool call).
5. The detail documents for the area being worked on (map below). Each ends with "Changes to other documents",
   answered questions, "To verify" and "Deferred or removed (simplification pass)".

## Document map

| Area | First-level plan | Detail documents |
|---|---|---|
| Platform | `plans/platform/plan.md` (deployables, zones, residency matrix, data protection §8a) | `data-model.md` (schemas, roles and grants, `SearchText` + built-in `swedish`, per-source documents and chunk sets, unpartitioned `Jobs`, outbox, bucket keys, site profiles, retention, blobs, Redis-down behaviour); `repo-and-deployment.md` (repo layout, build, Aspire, FakeGateway, stock pgvector image, pgBackRest, Compose, networks, secrets, settings rule §3, CI, **P0 checklist §11**) |
| Identity | `plans/identity/plan.md` | `design.md` (BFF copied from the gateway, RAG's own Entra registrations, member groups from claims (Graph lookup P2), `RagPrincipal`, `IAccessPolicy`, explain access, Open WebUI JWT trust, keys, per-level gateway keys and explicit ceiling, security events, Keycloak realm) |
| Gateway changes | `plans/gateway-changes/plan.md` | `design.md` (one combined release: residency ceiling + `RequireMaxResidency`, delegation with key-only request policy, claims-only member groups, 503 vs 403, `/v1/models` filtered by the ceiling; P2 rerank endpoint), `adr-drafts.md` (018–020, accepted together) |
| Parser | `plans/parser/plan.md` | `design.md` (Docling sidecar, every PDF incl. the pypdfium2 text-layer path, Tesseract `swe` only, `/v1/inspect`, gated `/v1/ner`, `format_deferred` for legacy Office/RTF/email) |
| Ingestion | `plans/ingestion-worker/plan.md` | `design-engine.md` (fixed linear pipelines, per-stage cache keys, jobs, scheduler, publish = plan → re-process → report → keep/roll back, index writer with per-document swap); `design-connectors.md` (URL list, crawl, shared frontier, sitemap `lastmod`, learned site profiles §5.6, egress guard, uploads by slot, ClamAV, deletion guard); `design-processing.md` (HTML extraction by site profile, chunking with breadcrumbs, personal data on chunk sets, advisory evaluation) |
| Knowledge API | `plans/knowledge-api/plan.md` | `design.md` (`/v1/retrieve`, hybrid search in three statements, lexeme OR query, scalar `CollectionId`, coverage needing vector and keyword weak, `IReranker` seam, citations, download grants) |
| Chat API | `plans/chat-api/plan.md` | `design.md` (OpenAI-compatible API, Open WebUI without task generation, condense skip + 600 ms timeout, residency checks before the first byte, same level every turn, bucket keys, feedback import via HTTP API) |
| Tools (P2) | `plans/tools/plan.md` | `design.md` (in-process tool hub, `hosted` servers only, `ume-mcp-kommun`, skills with served pointer + rollback, static small skills, sharing, third-party text, drift pre-registration, `/mcp` early) |
| Admin | `plans/admin/plan.md` | `design.md` (BFF + Swedish journey pages, generic forms for platform objects, test panel, nightly answer judge and skill gate in `rag-admin`, serialised config writes, cross-rules V1–V32 with V20/V27 retired, site-profile pages) |
| Cross-cutting | – | `plans/simplification-pass.md`; `plans/consistency-pass.md`; `plans/performance-pass.md` (capacity model incl. umea.se numbers, triggers); `plans/postponed-decisions.md` (register); `plans/to-verify.md` (≈ 160 checks by topic and phase) |

Background: `02-architecture-review.md` (R1–R7; R2 "shared document store" is now sharing by cache, not by row),
`platforms/` (Eneo, Dify, RAGFlow, Onyx, Open WebUI, Kernel Memory, OpenSearch, Bifrost; synthesis
`00-comparison.md`). Reference clones: `~/git/llmgateway`, `~/git/eneo`,
`~/git/rag-platforms/{dify,ragflow,onyx,open-webui,kernel-memory,bifrost}`.

## Key decisions (full list: Decisions table)

- **Services**: `Ume.Rag.Api` (roles chat, knowledge, mcp), `Ume.Rag.AdminApi` + Swedish admin UI (also hosts the
  nightly answer judge and, in P2, the skill gate), ingestion worker (`Worker:Classes` per instance; retrieval
  evaluation only, no tools), Python Docling parser (all PDFs, OCR, gated NER), ClamAV; renderer only in P2 if a
  source needs JavaScript; tool hub in-process in P2. Stock pgvector Postgres and Redis, separate from the gateway.
- **Ingestion**: documents and chunk sets per source, reuse by content-addressed caches; fixed linear pipelines
  (template + parameters), no canvas; **learned site profiles** per host (Sitevision preset) set the HTML stages,
  auto-applied with guardrails; sitemap `lastmod` incremental runs; advisory evaluation, no gates.
- **Gateway**: every model call goes through it, with a per-level delegating key and an explicit
  `x-ume-max-residency`; one combined gateway release before the pilot; embeddings always on-prem BGE-M3.
- **Classification**: `public` / `internal` / `sensitive`; residency public any, internal EU or on-prem, sensitive
  on-prem; `sensitive` isolation = a separate deployment when needed (no tier machinery).
- **Data protection**: chat content encrypted under (level, expiry month) bucket keys; destroying bucket keys waits
  for A1; pgBackRest PITR, write-once blobs, escrowed long-lived keys, `public` blobs unencrypted.
- **Pilot (P1)**: URL lists, umea.se crawl (≈ 14,100 HTML + PDF URLs; source `maxPages` ≥ 20,000), uploads;
  HTML, PDF, modern Office, ODF, images, text; RAG assistants in Open WebUI for one department; feedback; limited
  review by `rag-reviewer`.
- **Settings rule**: deployment knobs are `Section:Key`; run-time values are `Settings.<Section>.<Key>`.

## How we work (user preferences)

- **Ask before each level of detail**, with the question tool: up to 4 questions per round, recommended option
  first. Record every answer in the relevant document ("Answered") and in the Decisions table.
- **Verify a premise before putting it in a question** (and in a spec): in session 4 the Open WebUI feedback import
  turned out to carry no chat snapshots, which changed change 5 before it was briefed.
- **One subagent writes one document from a precise brief and does not commit.** The main session reviews, checks
  claims against the gateway and real package source, fixes problems and asks the user only what they must decide.
  A second subagent round carries the "Changes to other documents" rows into the other files.
- Wide passes run several subagents in parallel, each owning separate files and reporting cross-document issues
  instead of editing them; the main session settles conflicts in a binding follow-up brief (session 4:
  `simplification-pass.md` §11a). A Workflow needs the user's explicit opt-in.
- **Stay compatible with the gateway**: copy its conventions, files, claims, config keys, key format and
  security-event scheme; when something is missing, add it to both systems (`gateway-changes`).
- **Check library and site claims against real sources**: packages downloaded into the scratchpad and read, never
  executed; real pages fetched with `curl` (session 4: umea.se). Running external npm tools needs the user's
  permission (mermaid-cli was refused by the permission classifier).
- A formatter in the user's editor sometimes realigns markdown tables: **re-read a file before editing it**.
- Commit at the end of each reviewed step, then push to `origin main`.
- The global agent-memory instructions (`.agent/`) do not apply: this repo has no `.agent/` directory.

## Progress

| Step | Document | Commit |
|---|---|---|
| Research, general idea, review, first-level plans | `01-general-idea.md`, `02-architecture-review.md`, `platforms/`, `plans/*/plan.md` | `8160fcd` |
| 1–10. Detail steps | `plans/*/design*.md`, `data-model.md`, `repo-and-deployment.md` | `d889cd9` … `ec29ea9` |
| 11. Consistency pass + to-verify register | `plans/consistency-pass.md`, `plans/to-verify.md` | `55b6113` |
| Architecture diagrams | `plans/architecture.md` | `74f8b61` |
| Performance pass | `plans/performance-pass.md` | `3b79baa` |
| Overall review → simplification pass (10 changes) + umea.se site profiles | `plans/simplification-pass.md`, every plan document | session 4 commit |
| **P0 implementation** | `plans/platform/repo-and-deployment.md` §11 | **next** |

## Open items

**Waiting on people** (`plans/postponed-decisions.md`):
- **A1** archive law: are chat logs allmänna handlingar, is a gallringsbeslut needed? **Pilot blocker**; owner still
  to be named. Also gates destroying chat bucket keys.
- **W2** Open WebUI chat copies: v0.12.5 cannot prune other users' chats without `ENABLE_ADMIN_CHAT_ACCESS`; decide
  with A1 before the pilot.
- **W1** existing AI chat on umea.se (`chat.umea.se`, `ume-app-aichat-prod`): what it is and how it overlaps.
- **N1** product name, before the pilot.
- **U1** crawler about page and robots.txt check with the web editors, before the first production crawl.
- **P2** licences of models, OCR data, dictionaries and the `sv_SE` word list, before the pilot.
- **D1** Postgres streaming standby, before production beyond the pilot.
- **R1** self-hosted GitHub runner; **S2** object store server (P2).

**Measured or checked during implementation**: `plans/to-verify.md` (rows up to #160), including the decompounder
against Postgres `ispell`, the stock pgvector image tag, pgBackRest with that image, the umea.se site profile and
acceptance page, the cost of the per-document swap, and pool 12 / 4 searches.

## Next step: P0 implementation

P0 = a skeleton that builds, deploys, is observable and can be restored. Ordered checklist:
`plans/platform/repo-and-deployment.md` §11 (repo, projects, `verify.yml`, ServiceDefaults, stock pgvector image +
.NET decompounder test, migrations with outbox and unpartitioned `Jobs`, config-version library with the 90 s grace
window, FakeGateway with per-level keys and the 403/503 modes, Keycloak realm, keys, admin skeleton with serialised
writes, AppHost, parser contract, single-slot Compose, pgBackRest PITR restore drill, `release.yml`, test host, RAG's
own Entra registrations, the combined gateway release agreed, worker dispatcher).

Before starting:
1. Review the postponed-decisions register (phase-start rule) and the P0 rows of `plans/to-verify.md`.
2. Ask the user where the code lives (a new repo, or `src/` here), the order of the P0 items, and whether the combined
   gateway release starts in parallel in the gateway repo.
3. Apply the 7-day package rule (V1) when pinning versions.
