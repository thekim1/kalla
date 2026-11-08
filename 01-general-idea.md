# Knowledge & assistant services: general idea

Status: the goal and the design principles of 2026-10-29, still the basis of the design. Builds on the Umeå kommun LLM
gateway (<https://github.com/thekim1/llmgateway>, local clone `~/git/llmgateway`). What binds is
[`plans/decisions.md`](plans/decisions.md), then the design documents ([`plans/README.md`](plans/README.md)).

## 1. Goal

Give the municipality a base platform for **retrieval (RAG), crawling and chatbots**, the way the LLM gateway is the base
platform for model access, keys and budgets:

- Departments add **specific URLs / websites**, **SharePoint** sites and libraries and **files** as knowledge sources.
- They build **assistants** on top of those sources, for staff (internal) and for citizens (public website).
- Other applications (intranet, Teams, case systems, Open WebUI, a web widget) use the assistants and the retrieval
  through **APIs**.
- Easy to develop and easy to administer; deployable with Docker on **Azure or on-prem** with the same artifacts.

## 2. Design principles (learning from Eneo)

Eneo covers the same ground but is one large platform (chat UI, assistants, crawler, integrations, apps, workflows in
one deployable) and is slow. We aim for the opposite:

1. **Small services, one job each, standard APIs.** No service owns a UI it does not need. Chat front ends are clients.
2. **Data plane and control plane are separate**, as in the gateway: the APIs apps call never host admin endpoints, the
   admin API never sits in the request path.
3. **Every model call goes through the gateway** (chat and embeddings). No provider credentials, no routing, no
   budgets in these services. PII routing, residency and cost reporting come for free.
4. **API first, OpenAI-compatible where it exists.** An assistant is exposed as a "model" on `/v1/chat/completions`, so
   any OpenAI client works unchanged.
5. **Fast request path.** Retrieval is one Postgres query (hybrid search) with warm caches (as designed: three short
   statements, keyword in parallel with the query embedding, fused in .NET; `plans/knowledge-api/design.md` §6);
   ingestion is entirely off the request path in a worker.
6. **Same stack and ops model as the gateway**: .NET 10, Aspire for local dev, PostgreSQL, Redis, Vue 3 admin UI with
   the gateway design system, OIDC/BFF login, Docker Compose release bundle, Kubernetes optional.
7. **Content is stored here, unlike the gateway.** Documents, chunks and chat history are personal-data-bearing, so
   retention, deletion and audit are first-class features, not afterthoughts.
8. **Failures are visible, never silent.** Every document has a run record with outcome and reason; a sync that
   loses content is shown as degraded, and large deletions need confirmation (Onyx deleted 189,833 documents and
   reported success, see `platforms/onyx.md`).
9. **Small footprint.** Pilot target: the whole platform on one 8 vCPU / 16–24 GB host in two resource zones
   (Dify ships 16 containers, Onyx 13 with ≥10 GB RAM). Services are one binary that can run in several roles.
10. **Built for Swedish.** Swedish full-text search with compound-word splitting and measured Swedish recall. None of
    the platforms analysed does this.

Evidence and the full flaw table: `platforms/00-comparison.md`.

## 3.–9. and 11. First cut (archived)

The first sketch of services, domain model, ingestion, pipelines, retrieval, identity, front ends, technology and the
open questions is in [`plans/archive/general-idea-first-cut.md`](plans/archive/general-idea-first-cut.md), with its
section numbers. The design documents replaced it.

## 10. Decisions (moved)

The decisions in force are [`plans/decisions.md`](plans/decisions.md). The table that stood here, with every dated
row, is [`plans/archive/decisions-log.md`](plans/archive/decisions-log.md).
