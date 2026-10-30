# Plans

One folder per service or cross-cutting area, each with a `plan.md` (first-level plan) and the detail documents of
detail steps 1–10 (`platform/data-model.md`, `platform/repo-and-deployment.md`, `*/design*.md`,
`gateway-changes/adr-drafts.md`); where a plan and its design differ, the design wins. Background:
[`../01-general-idea.md`](../01-general-idea.md) (design and decisions),
[`../platforms/00-comparison.md`](../platforms/00-comparison.md) (lessons from Eneo, Dify, RAGFlow, Onyx, Open WebUI,
Kernel Memory and OpenSearch) and [`../02-architecture-review.md`](../02-architecture-review.md) (improvements R1–R7,
now applied to these plans).

Decisions put off on purpose, with their stopgaps and deadlines: [`postponed-decisions.md`](postponed-decisions.md)
(reviewed at the start of every phase). Package, behaviour and estimate checks from every document's "To verify"
section, with phase and method: [`to-verify.md`](to-verify.md).

Working name for the platform and its code: **Ume.Rag** (placeholder, to be renamed).

| Folder | Area | Plane | Owns |
|---|---|---|---|
| [`platform/`](platform/plan.md) | Repo layout, resource zones, data ownership, config versioning, classification, retention, evaluation, phases | – | Conventions, migrations, Postgres image, release bundle |
| [`identity/`](identity/plan.md) | Entra ID authentication and authorisation, client registry, MCP auth, on-behalf-of | cross-cutting | App registrations, access policy |
| [`knowledge-api/`](knowledge-api/plan.md) | Hybrid retrieval with citations | data | Read access to the collection index; search interface |
| [`chat-api/`](chat-api/plan.md) | OpenAI-compatible chat, assistants as models, history | data | Conversations, messages, feedback |
| [`tools/`](tools/plan.md) | Skills, tool hub, platform MCP server, hosted municipal MCP servers | data | Tool registry, tool credentials, tool-call audit |
| [`ingestion-worker/`](ingestion-worker/plan.md) | Connectors, pipeline engine, auto-parse, embedding, evaluation | background | Document store, collection index, runs, jobs, parse profiles, evaluation sets |
| [`parser/`](parser/plan.md) | Python + Docling, stateless | internal | Nothing (stateless) |
| [`admin/`](admin/plan.md) | Configuration, pipeline canvas, evaluation, clients, audit | control | Configuration (`config` schema) |
| [`gateway-changes/`](gateway-changes/plan.md) | Residency ceiling, department member groups, delegated attribution in the LLM gateway (releases B → C → A) | gateway repo | – |

## Phases (all areas)

| Phase | Goal | Scope |
|---|---|---|
| **P0 Foundation** | Skeleton that builds, deploys and is observable | Repo, Aspire AppHost, ServiceDefaults, migrations with config outbox, Postgres image with pgvector + Swedish Hunspell, resource zones, Keycloak with the gateway's claims, gateway-style keys, fake gateway, CI; approval package 1 (admin + Open WebUI sign-in) submitted; gateway-changes ADR |
| **P1 MVP / pilot** | One pilot department uses an assistant in Open WebUI | URL list, website crawl, file upload into the shared document store; templates + forms, preview; heuristic auto-parse; on-prem platform embedding profile; generated evaluation sets + publish gate; collection go-live floor; hybrid retrieval; RAG assistants (prompt + collections; skills arrive in P2); Chat API with citations, residency check, 90-day history; feedback with Open WebUI import and the feedback page; limited conversation review by `rag-reviewer` (behind a feedback item); `GatewayModels` sync; client registry with Open WebUI at ceiling `internal`; gateway releases B → C → A (residency ceiling, member groups, delegation) |
| **P2 Broaden** | Several departments, internal sources, tools | SharePoint, visual canvas, learned parse profiles (auto-apply, evaluation-driven rollback), skills + agent assistants, tool hub with read-only tools, platform MCP server, MCP server template + first municipal server, public widget, full conversation review (search, `sensitive` two-person flow), chat attachments (chat-scoped) |
| **P3 Scale and refine** | Driven by measurements | Swedish benchmark (curated evaluation layer), rerank, OpenSearch if a trigger fires, query-side canvas, Teams client, Entra OBO for user-delegated tools, write tools with confirmation |

Order within P1: platform + identity → gateway changes (parallel, gateway repo) → parser + ingestion worker →
knowledge API → chat API → admin pages → crawler → evaluation gate → Open WebUI pilot.
