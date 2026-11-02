# Architecture plans: knowledge, assistants and models

Status: proposal, 2026-11-02. Based on `main` at `0525053`, the current Decisions table and detail documents,
inspection of the retrieval, assistant and access code, and the references below. This is a design recommendation,
not a new measurement or an implementation brief. It is not binding until adopted in `01-general-idea.md` §10.
Rewriting this proposal does not adopt its scope changes. Items marked **changes a decision** need adoption before
implementation; items marked **fits current decisions** still need a scheduled checklist item if none exists.

## 1. Assessment

**Keep the service architecture.** Chat and knowledge share `Ume.Rag.Api`; the admin control plane, ingestion worker
and parser have useful separate responsibilities. Every model call goes through the gateway with an explicit
residency ceiling. Postgres remains the search store and job queue until measured scaling triggers justify a change.
The improvements proposed here do not require new services, another vector database or an orchestration framework.

**Build the product around reusable knowledge and purpose-specific assistants.** Models are execution dependencies;
knowledge bases are governed content; assistants provide a maintained experience over that content. Departments
should work in three concepts: **our content, our assistant, where people use it**. Sources, profiles, pipelines,
keys and model selection remain available to the people responsible for them without becoming mandatory steps for
every department admin.

The current foundation is suitable. The main gaps are proving answer quality, completing shared knowledge and
access control, and making assistant setup express a purpose rather than expose platform configuration.

## 2. What to do, in order

| # | Item and evidence of completion | Phase | Decision status |
|---|---|---|---|
| 1 | **Complete shared knowledge and topics**, P1b items 33–38 (#318): the department running umea.se owns one knowledge base; another department links selected topics without copying content or gaining source-admin access. Show owner and contact; hide teams in create forms; provide the Map as a table. | P1b, first | **Already decided**: Decisions "Organisation …". |
| 2 | **Diagnose the measured retrieval misses and complete the human gold set.** Record a cause and proposed remedy for each of the seven real misses; compare remedies on the same questions. Calibrate coverage with answerable and unanswerable human questions and complete the existing hand-graded answer gate. | P1b, alongside item 1 | **Fits current decisions**; gold-set calibration is item 21. |
| 3 | **One guided assistant setup with three fixed presets.** Choose owned or shared knowledge and topics, or add a source; choose a purpose, test and publish. A department can complete the journey without selecting a model or editing pipeline JSON. Presets: Citizen service, Internal policy Q&A, Case-handling support. | Proposed P1b, on the organisation contracts | **Changes a decision**: brings a fixed subset of assistant templates forward from P2. No template editor. |
| 4 | **Reduce department-facing settings.** Show scope, schedule, contact and permitted retention choices where relevant; keep model administration and general retrieval/crawl tuning on platform pages. Existing advanced rights still apply. | P1b, UI | **Fits current decisions**; changes visibility, not policy limits. |
| 5 | **Complete the W3 access investigation before broader departmental use.** Demonstrate consistent model visibility and invocation decisions for two departments; prove that a direct request cannot bypass a hidden model. Use the existing per-department Open WebUI fallback if necessary. | P1b spike; broader use follows the existing P2 gateway prerequisites | **Already decided**: W3 now, fallback until resolved. This does not bring gateway delegation into P1b. |
| 6 | **Close the content-maintenance loop.** Give the responsible owner feedback and not-found counts, affected knowledge links and available permitted metadata. Support editor-rewritten, personal-data-free test questions with provenance. Show unavailable question text as unavailable. | P1b after contacts | **Fits current decisions** within A1 and the feedback-rewrite rule; no new raw-chat storage. |
| 7 | **Explain how to use an assistant.** Provide its purpose, content scope, owner, access requirements, OpenAI-compatible example and an authorised Knowledge API example; explain how to request a key. Generate examples without secrets. | P1b | **Fits current decisions**; examples must match each API's actual caller permissions. |
| 8 | **Evaluate each assistant's complete behaviour.** Maintain representative questions and expected outcomes for its combined knowledge and instructions; report correctness, supported citations, refusal behaviour, follow-ups, latency and cost where available. | P2; the pilot's existing human answer gate remains P1b | **Fits current timing** for per-assistant test sets and answer evaluation. Earlier automation would change a decision. |
| 9 | **Run an internal-content pilot.** Start with approved uploads and an explicitly defined audience; use results to establish the need for SharePoint and its permission model. | P2 broadening; an earlier internal pilot needs an explicit scope decision | SharePoint remains **P2**. Its connector is not implemented. |

## 3. Domain responsibilities

| Concept | Responsibility | Example | Lifecycle |
|---|---|---|---|
| Source | Origin, fetch credentials, sync scope and freshness | A website section or uploaded files | Sync and processing runs |
| Knowledge base | Owned, classified, governed searchable content | Municipal website; HR policies | Content updates, processing configuration and publication |
| Assistant | Purpose, instructions and permitted knowledge scope | Building-permit guidance | Draft, test, publish, restore |
| Model | Execution dependency reached through the gateway | Answer, embedding or judge model | Platform approval and role-specific validation |
| Preset | Starting configuration for an assistant | Citizen service | Platform-maintained starting point |
| Placement, later | Availability and restrictions for one channel | Staff chat; public widget | Independently audited channel configuration |

These are domain responsibilities, not a proposal for a new service per row. The UI need not expose every object.
A source refresh does not create an assistant version. An answer-model change does not rebuild the knowledge base.
An embedding-model change does require a compatible new index of the affected content.

### Assistants represent a job

Keep **Assistant** as the user-facing name. Define one by the questions it helps with, the knowledge it may use,
how it responds and what it does when evidence is insufficient. "Building-permit guidance" expresses a useful
service. A provider model name and collection id describe its implementation.

Keep behaviour and knowledge scope versioned together: instructions, linked collections/topics, answer-model
selection and query configuration. Add representative questions when P2 assistant test sets arrive. Published
assistants should have a responsible owner and an understandable scope.

The OpenAI-compatible API continues to expose assistants as `model` identifiers. That adapter convention does not
make them underlying language models. Admin screens should distinguish the two consistently.

### Presets make setup easier

A preset supplies suggested instructions, classification and an approved model default for a purpose. It must not
lower the classification required by linked content or override access, residency or history policy. Departments
can edit the purpose and wording; model overrides belong to users with the relevant advanced permission.

For the proposed fixed presets, copy the resolved settings into the draft. Editing a preset later must not silently
change a published assistant. Reusing existing draft/version storage avoids a new template-management subsystem.

### Knowledge is reusable without creating an assistant for every topic

Finish the decided department-owned sharing model. An assistant can combine shared municipal topics with its own
department's approved knowledge without another crawl or content copy. Sharing grants read-only use through
assistants, not source administration.

Do not automatically create persistent assistants for every knowledge base and topic. That would multiply model
picker entries, owners, evaluations and retirement work. Offer **Ask this knowledge base** as a convenience after
W3, implemented through the same authorised answer path. Create a named, persistent assistant when a team needs a
maintained purpose, combined sources, special instructions or an integration endpoint. The convenience action's
implementation and timing need a brief before work starts; it must not bypass the decided sharing boundary.

## 4. Models and retrieval profiles

Keep model selection behind the gateway and the platform's approved catalogue. Departments normally choose a
purpose and receive a suitable default. Treat model roles separately rather than assuming that one model is right
for every stage.

| Role | Required validation when changed |
|---|---|
| Answer generation | Correctness, supported citations, refusal behaviour, latency, cost and residency |
| Follow-up condensation | Preservation of meaning, follow-up retrieval, timeout and raw-question fallback |
| Embedding | Query/document compatibility, re-embedding, retrieval quality and coverage recalibration |
| Reranking, when enabled | Relevance improvement against added latency and cost |
| Question generation and judging | Agreement with human-reviewed examples, language and evaluation bias |

Presets should resolve to explicit model selections for publication. If a gateway route can change its underlying
provider model, record the resolved model when the gateway supplies it and re-evaluate meaningful route changes.
Do not invent a second routing or fallback policy that conflicts with the gateway's contract.

### Embedding identity is more than dimensions

Two models producing 1,024-dimensional vectors are not necessarily compatible. Preserve the embedding-profile
boundary and make its identity include the model and relevant encoding configuration. Query and document vectors
must use compatible profiles. Changing embedding space requires re-embedding affected content; changing its
processing also needs evaluation and coverage recalibration.

Today `KnowledgeService` selects one embedding profile and skips linked collections on other profiles. Before
supporting mixed profiles or an embedding-model migration, reject unsupported assistant combinations explicitly
rather than silently searching only part of their knowledge. A future multi-profile search must embed per profile
and fuse rankings; raw distances from different spaces are not comparable. This is a future design requirement,
not a request to implement multi-profile retrieval now.

## 5. Access, channels and placements

Keep three permissions distinct:

- A department's right to link a shared knowledge base.
- A person or application's right to use an assistant.
- A caller's right to retrieve content or download a cited original.

Enforce the relevant scope before content enters a prompt. Instructions and topic labels do not grant permission.
Apply the same invocation policy when listing assistants and handling direct requests; hiding a model is not access
control. Anonymous, application and user callers still have different credential and scope requirements.

Open WebUI's current forwarded-user representation carries `sub`, `name`, `email` and `role`, without department
membership. Department-specific access therefore needs the W3 trust and integration work, not just a filter added
to `/v1/models`. The client-level listing without a user JWT also needs an explicit outcome in that investigation.

### Placements when channel policies diverge

The current split already exists in part: `AssistantVersions` stores behaviour and knowledge; retention, history
mode and allowed origins live on `Assistants`; audiences live in their own table linked to the assistant. Access
changes do not require a new behaviour draft today.

Introduce **Placement** when a second channel needs independent access or operating settings for the same assistant.
An assistant keeps its published behaviour and knowledge scope. A placement identifies a client/channel and its
audience, origins, permitted history/retention and applicable limits. It changes independently, with audit and config
invalidation. Effective permission and residency remain constrained by platform, client and content policies.

Before adopting the split, specify how a request selects a placement, how keys and conversation ids bind to it,
how the assistant's external identifier stays unambiguous, and how access revocation affects existing conversations.
This is a data-model/API change, not a new service. Before production it can be made in one step under the existing
no-backward-compatibility decision. Stored history still depends on A1.

### Internal sources need an explicit audience

The planned SharePoint connector does not mirror document ACLs: access is per collection. Start with content
explicitly approved for that collection's audience. A SharePoint pilot must not imply that original document
permissions automatically survive ingestion. Permission-mixed libraries would require a separate decision and
retrieval-time authorization design; a warning about unique permissions alone cannot enforce those permissions.

## 6. Quality and context

Use two connected evaluation layers. Knowledge evaluation finds extraction, freshness, retrieval and missing-evidence
problems. Assistant evaluation finds incorrect answers, unsupported claims, poor refusal decisions and follow-up
failures over the assistant's actual combined scope. Retrieval recall is useful but does not prove answer quality.
The existing human go-live gate remains the pilot's acceptance test; automated publish evaluation remains advisory.

The first generated set reports recall@10 0.91 and MRR 0.70. Latest recorded analysis finds seven of nine misses are
real misses, including statistics tables, document templates and school rules; only two are near-duplicate
förskola pages. For each miss inspect the extracted text, chunk context, expected document, candidate ranking and
final context. Do not assume a stronger answer model or page-family grouping will fix missing evidence.

Check whether chunks preserve title, section, table headers, applicable year and responsible organisation. Heading
prefixes already exist. Test further enrichment only where a diagnosed failure justifies it, and compare on the
same evaluation set before broad rollout. Keep the current per-document diversity limits; add cross-document
near-duplicate handling only if measured citation or ranking problems justify it.

Calibrate `coverageMaxDistance` and the keyword floor from human answerable **and** unanswerable questions. Generated
answerable questions alone cannot establish the refusal boundary. Report outcomes for both groups; similarity is
an evidence-strength signal, not a probability that the answer is correct. Revalidate when embedding profiles,
content scope or retrieval behaviour changes. Full answers can still lack required evidence even when one retrieved
chunk crosses the coverage threshold.

Keep the predictable retrieval-and-answer path for ordinary questions. Consider multi-step or agentic retrieval
when measured workloads require comparison, decomposition or several searches. Tools and future agent behaviour
remain within the existing phased plan; ordinary assistants should not require an agent framework.

## 7. Scope and next steps

Adopt the domain responsibilities and the desired department journey first. Decide whether the fixed presets and
guided path move into P1b. Record adopted changes in the Decisions table and update the relevant detail documents;
this proposal does not replace those sources of truth.

For implementation, retain one checklist item or smaller per PR with observable completion evidence. B's
organisation schema and contracts unblock the shared-content journey; A's topic filtering follows the agreed
hand-offs. Diagnose the measured retrieval misses and finish the gold set alongside that work. Broader departmental
use waits for the agreed access and gateway prerequisites. Assistant evaluation, internal-source expansion and
placements follow the triggers and phases above.

Avoid new services for these domain concepts, automatic assistant proliferation, a general preset editor for three
fixed choices, model routing duplicated outside the gateway, premature multi-profile search, and a visual pipeline
builder before its existing trigger. A project/workspace owner type remains deferred until a real use case justifies
changing the organisation decision.

### References

Repository evidence: [assistant entities](src/Ume.Rag.Domain/Entities/AssistantEntities.cs),
[knowledge service](src/Ume.Rag.Answering/KnowledgeService.cs),
[Open WebUI user validation](src/Ume.Rag.Api/OpenWebUiUserJwt.cs),
[access policy](src/Ume.Rag.Domain/Access/AccessPolicy.cs),
[coverage evaluation](src/Ume.Rag.Retrieval/CoverageEvaluator.cs),
[result diversity](src/Ume.Rag.Retrieval/ResultDiversifier.cs), and [current handover](HANDOVER.md).
Binding details remain in the [Decisions table](01-general-idea.md), [organisation proposal](plans/admin/organisation-proposal.md),
[knowledge design](plans/knowledge-api/design.md), [chat design](plans/chat-api/design.md),
[connector design](plans/ingestion-worker/design-connectors.md), and [phase plan](plans/README.md).

External references support the evaluation and context recommendations, not a claim of measured improvement here:
[Microsoft RAG design and evaluation](https://learn.microsoft.com/en-us/azure/architecture/ai-ml/guide/rag/rag-solution-design-and-evaluation-guide),
[Microsoft retrieval and answer evaluators](https://learn.microsoft.com/en-us/azure/foundry/concepts/evaluation-evaluators/rag-evaluators?view=foundry),
and [Anthropic contextual retrieval](https://www.anthropic.com/research/contextual-retrieval).
