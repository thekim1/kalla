# Plans

Ume.Rag's plans in three layers. Read them in this order:

1. **Decisions** — [`../01-general-idea.md`](../01-general-idea.md) §10. Rows with status `current` bind and win over
   everything below.
2. **Roadmap** — [`roadmap.md`](roadmap.md): what is done, what is left before go-live, what P2 and P3 hold, and what
   waits on people. One page.
3. **Work** — [`work/`](work/): one queue per stream ([`stream-a.md`](work/stream-a.md),
   [`stream-b.md`](work/stream-b.md)) and the main session's ([`main.md`](work/main.md)). Each item says what to do,
   where, its done-when, what it waits on and which design sections to read. Start prompts: [`work/prompts/`](work/prompts/).

The **design documents** are the specification the code follows. Open the one for your area when an item points to it;
nobody needs to read them end to end.

| Area | Design | What it covers |
|---|---|---|
| Platform | [`platform/data-model.md`](platform/data-model.md), [`platform/repo-and-deployment.md`](platform/repo-and-deployment.md) | Schemas, grants, retention; repo layout, images, Compose, CI, backups |
| Identity | [`identity/design.md`](identity/design.md) | Keycloak and later Entra ID, clients and keys, access decisions |
| Ingestion | [`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md), [`design-connectors.md`](ingestion-worker/design-connectors.md), [`design-processing.md`](ingestion-worker/design-processing.md) | Jobs, publish and rollback; crawler, URL lists, uploads; parsing, chunking, embedding, evaluation |
| Parser | [`parser/design.md`](parser/design.md) | The Python/Docling service: formats, OCR, limits |
| Knowledge | [`knowledge-api/design.md`](knowledge-api/design.md) | Hybrid retrieval, citations, the not-found gate, topics |
| Chat | [`chat-api/design.md`](chat-api/design.md) | OpenAI-compatible chat, frames, condense, feedback, history |
| Admin | [`admin/design.md`](admin/design.md) | The admin API, validation rules (V-numbers), every admin page |
| Tools (P2) | [`tools/design.md`](tools/design.md) | Skills, the tool hub, MCP |
| Gateway changes | [`gateway-changes/design.md`](gateway-changes/design.md), [`adr-drafts.md`](gateway-changes/adr-drafts.md) | Residency ceiling, delegation, member groups in the LLM gateway |
| Cost attribution | [`platform/cost-attribution.md`](platform/cost-attribution.md) | Who pays for each model call: Ume.Rag's metering first, the gateway's delegated attribution as the goal |
| Upload quality | [`platform/upload-quality-review.md`](platform/upload-quality-review.md) | Assessment (not binding): how well uploaded files are handled, the gaps U1–U7 and a proposed order |
| Keys and budgets per assistant | [`platform/assistant-keys-and-budgets-options.md`](platform/assistant-keys-and-budgets-options.md) | Options for a decision (not binding): Ume.Rag keys scoped to an assistant, and how a per-assistant budget works with the gateway |

Registers, kept current:
- [`postponed-decisions.md`](postponed-decisions.md): decisions put off on purpose, with stopgap, deadline and owner.
- [`to-verify.md`](to-verify.md): the open package, behaviour and estimate checks; closed rows, with their results, are
  in [`archive/to-verify-closed.md`](archive/to-verify-closed.md) under the same numbers. The streams record new
  results in `work/results-a.md` and `work/results-b.md`.
- [`architecture.md`](architecture.md): diagrams (system overview, chat request, ingestion, tool call).
- [`gold-set/`](gold-set/README.md): the draft gold set for the go-live gate.

**History** is in [`archive/`](archive/): the finished phase plans (`p0/`, `p1/`, `p1b/`), the cross-cutting passes
(`passes/`, folded into the designs on 2026-11-01; each ends with a ledger of where its rules went), the first-level
area plans (`area-plans/`, overtaken by the designs) and older proposals and reviews. Nothing there binds, except the
performance pass's capacity model as named in the roadmap.

Working name: **Ume.Rag** (placeholder, register N1). Plain-language overview for stakeholders:
[`../docs/stakeholder-report.html`](../docs/stakeholder-report.html).
