# Handover

Status of the Ume.Rag design work, for the next session. Updated 2026-10-29 (end of session 2, steps 3–6a).

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
- Commit only when asked or at the end of a reviewed step; the repo is `~/research/rag` (branch `master`).
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
| **6b. Connectors** | `plans/ingestion-worker/design-connectors.md` | **next** | – |
| 6c. Processing (auto-parse, chunking, embedding, PII, evaluation) | `plans/ingestion-worker/design-processing.md` | todo | – |
| 7. Knowledge API | `plans/knowledge-api/` | todo | – |
| 8. Chat API (decide C1 chat attachments here) | `plans/chat-api/` | todo | – |
| 9. Tools (skills, tool hub, MCP server, template) | `plans/tools/` | todo | – |
| 10. Admin API + UI | `plans/admin/` | todo | – |

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

## Next step in detail: step 6b, connectors

Brief a subagent to write `plans/ingestion-worker/design-connectors.md`, consuming the contracts of
`design-engine.md` (root nodes `http-fetch`/`crawl`/`upload`, `sync`/`fetch` jobs, `EnsureCurrent`, run outcomes,
index writer `SoftDeleteReferencesAsync`). Cover: the capability interfaces (plan §3) as C# contracts; canonical
identity and URL normalisation; URL list with conditional GET; website crawl (sitemap, include/exclude, depth/page
limits, robots.txt, user agent, the **shared crawl frontier** and per-host politeness in `CrawlHosts`, answered 4),
SSRF/egress guard (`IEgressGuard`, firewall rules, redirects, DNS rebinding), the `rag-renderer` (when to render,
JavaScript-only detection, and **how rendered and non-rendered raw content of the same URL coexist** — open item from
6a §13.1); uploads (admin → blob → ClamAV → sync), **email attachments as child documents** (parser design §5,
data-model `ParentDocumentId`, depth ≤ 2); change detection (ETag/Last-Modified, content hash), source-setting effect
tags (identity/scope/behaviour/cosmetic) and the smallest re-sync; the **deletion guard** (20 %, empty source, 14-day
grace, confirm/reject commands); malware handling and quarantine; checkpoints and resume; SharePoint (P2) design
outline with the Onyx/Eneo issues as acceptance tests; observability, tests. Before briefing, ask the user anything only
they can decide (e.g. crawler user-agent text and contact, whether to honour `noindex`/`nofollow`, max pages per site).

Then **6c** (`design-processing.md`): auto-parse heuristics and range planning (parser design §6, §8), stitching,
cleaning, split + chunk (answered 8), PII detection incl. the **parser NER endpoint with its own lane** (answered 6,
engine §18/§21), embedding (profile, cache, batching, AIMD), and evaluation sets + publish gate (agree with the
engine's **shadow chunk sets**, engine §12.1).
