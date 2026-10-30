# Plans

One folder per service or cross-cutting area, each with a `plan.md` (first-level plan) and the detail documents of
detail steps 1–10 (`platform/data-model.md`, `platform/repo-and-deployment.md`, `*/design*.md`,
`gateway-changes/adr-drafts.md`); where a plan and its design differ, the design wins. Background:
[`../01-general-idea.md`](../01-general-idea.md) (design and decisions),
[`../platforms/00-comparison.md`](../platforms/00-comparison.md) (lessons from Eneo, Dify, RAGFlow, Onyx, Open WebUI,
Kernel Memory and OpenSearch) and [`../02-architecture-review.md`](../02-architecture-review.md) (improvements R1–R7,
now applied to these plans).

Cross-cutting passes, in order: [`consistency-pass.md`](consistency-pass.md) (detail step 11),
[`performance-pass.md`](performance-pass.md) (≈ 10× the pilot on 3–5 hosts: capacity model, bottlenecks, topologies,
scaling triggers, scale test) and [`simplification-pass.md`](simplification-pass.md) (thin P0/P1, fixed pipelines,
stock Postgres, backups and key escrow, residency by structure, one gateway release, advisory evaluation, umea.se
findings and learned site profiles). Where the simplification pass and an older passage differ, the simplification
pass wins.

Decisions put off on purpose, with their stopgaps and deadlines: [`postponed-decisions.md`](postponed-decisions.md)
(reviewed at the start of every phase). Package, behaviour and estimate checks from every document's "To verify"
section, with phase and method: [`to-verify.md`](to-verify.md).

Architecture diagrams (Mermaid: system overview, chat request, ingestion and publishing, agent tool call):
[`architecture.md`](architecture.md).

Working name for the platform and its code: **Ume.Rag** (placeholder, to be renamed).

| Folder | Area | Plane | Owns |
|---|---|---|---|
| [`platform/`](platform/plan.md) | Repo layout, resource zones, data ownership, config versioning, classification, retention, backups, evaluation, phases | – | Conventions, migrations, stock pgvector Postgres with pgBackRest, release bundle |
| [`identity/`](identity/plan.md) | Entra ID authentication and authorisation (RAG's own registrations), client registry, MCP auth, on-behalf-of | cross-cutting | App registrations, access policy |
| [`knowledge-api/`](knowledge-api/plan.md) | Hybrid retrieval with citations (.NET decompounding, not-found gate, reranker seam) | data | Read access to the collection index; search interface |
| [`chat-api/`](chat-api/plan.md) | OpenAI-compatible chat, assistants as models, history | data | Conversations, messages, feedback |
| [`tools/`](tools/plan.md) | Skills, in-process tool hub, platform MCP server, hosted municipal MCP server | data | Tool registry, tool credentials, tool-call audit |
| [`ingestion-worker/`](ingestion-worker/plan.md) | Connectors, fixed pipelines, learned site profiles, auto-parse rules, embedding, evaluation harness | background | Documents and chunk sets per source, collection index, runs, jobs, parse rules, site profiles, evaluation sets |
| [`parser/`](parser/plan.md) | Python + Docling, stateless; every PDF (pypdfium2 fast path, Docling, Tesseract OCR) | internal | Nothing (stateless) |
| [`admin/`](admin/plan.md) | Configuration, department journey pages, template parameters, evaluation reports, clients, audit | control | Configuration (`config` schema) |
| [`gateway-changes/`](gateway-changes/plan.md) | Residency ceiling, delegated attribution and department member groups (claims-only) in the LLM gateway, in **one combined release**; a rerank endpoint as a separate P2 change | gateway repo | – |

## Phases (all areas)

| Phase | Goal | Scope |
|---|---|---|
| **P0 Foundation** | Skeleton that builds, deploys, is observable and can be restored | Repo, Aspire AppHost, ServiceDefaults and health, migrations with config outbox, config-version library with the grace window, stock pgvector Postgres image + .NET decompounder test, pgBackRest with WAL archiving and a point-in-time restore drill, resource zones, Keycloak with the gateway's claims, gateway-style keys, FakeGateway, parser contract, worker dispatcher skeleton, admin UI skeleton and write path, single-host Compose, `verify.yml` + `release.yml` + image test, deploy to the test host; scale seams (pooler-safe SQL, write-once `IBlobStore`, `IJobQueue`, `Worker:Classes` and `Worker:Concurrency:<class>`); Entra package 1 (RAG's own registrations) submitted; combined gateway release agreed (ADR drafts 018–020) |
| **P1 MVP / pilot** | One pilot department uses an assistant in Open WebUI | URL list, website crawl (sitemap `lastmod` incremental runs by default; learned site profiles with a Sitevision preset), file upload; one fixed pipeline per source type with template parameters, preview; formats HTML, PDF (text layer and OCR, every PDF through the parser), modern Office, OpenDocument, images (Tesseract `swe`), text, Markdown, CSV; auto-parse rules; personal-data patterns with a gated name check; on-prem platform embedding profile; hybrid retrieval with a not-found gate and an `IReranker` seam; advisory evaluation harness (publish = plan → re-process → report → keep or roll back; nightly runs; answer-level judge in `rag-admin` from late P1); RAG assistants (prompt + collections; skills arrive in P2); Chat API with citations, explicit residency ceiling on every gateway call, 90-day history; Open WebUI title, tag and follow-up generation off; feedback with Open WebUI import (HTTP API) and the feedback page; limited conversation review by `rag-reviewer` (behind a feedback item); `GatewayModels` sync; client registry with Open WebUI at ceiling `internal`; department scope from groups in the claim; Swedish admin pages for the department journey (generic forms for platform objects); `perf.yml` (R1) and nightly evaluation; one combined gateway release (residency ceiling, delegation, member groups claims-only); single host, one slot |
| **P2 Broaden** | Several departments, internal sources, tools; first multi-host deployment | SharePoint; renderer (only if pilot-start checks show sources need JavaScript); legacy `.doc`/`.xls`/`.ppt`, RTF and e-mail, archives and macro-enabled Office (register Z1); parse-profile proposals with platform-admin approval; rerank (a separate P2 gateway release with an endpoint for `bge-reranker-v2-m3` on-prem); Graph member-group lookup with data-plane audiences and approval package 2; skills + agent assistants (advisory skill gate run in `rag-admin`); `/mcp` with `search_knowledge` and `list_collections` early; in-process tool hub (`hosted` servers only) with read-only tools; first municipal server `ume-mcp-kommun` (mock fee calculator); sharing with named departments; public widget; full conversation review (search, `sensitive` two-person flow); chat attachments (chat-scoped); multi-host topology with its seams (`LeaderLeases`, connection-budget script, batched dequeue, debounced `NOTIFY`, Redis crawl and embedding leases, a/b slots); Postgres streaming standby before production beyond the pilot; `flake-hunt.yml`, `scale.yml`, parser fuzzing |
| **P3 Scale and refine** | Driven by measurements | Swedish benchmark (curated evaluation layer), OpenSearch if a trigger fires, separate `rag-tools` service with the first external, OBO or write tool, `municipal`/`external` tool hosting and tool egress, per-skill MCP endpoints and `ask_assistant`, Teams client, Entra OBO for user-delegated tools, write tools with confirmation |

Order within P1: pilot-start checks (source inventory: legacy formats, JavaScript need; the existing umea.se chat,
register W1) → platform + identity → combined gateway release (parallel, gateway repo; the pilot waits for it) → parser
+ ingestion worker → knowledge API → chat API → admin journey pages → crawler with site profiles (umea.se acceptance
page) → evaluation harness and `perf.yml` → Open WebUI pilot (A1 and W2, Open WebUI chat-copy retention, decided before it).
