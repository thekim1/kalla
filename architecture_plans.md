# Architecture plans: usefulness and abstractions

Status: proposal, 2026-11-02 (second version, checked against the code on `main` at `1fe1067`). Not binding until a
Decisions row in `01-general-idea.md` §10 adopts it. Items marked **changes a decision** reverse or move a `current`
row and need the user's call.

## 1. Assessment

**Architecture: sound, keep it.** Small services (chat and knowledge in one `Ume.Rag.Api`), the control plane apart
from the request path, every model call through the gateway, Postgres as the only search store, worker and parser in
their own containers. The cost of change is not in the backend.

**Usefulness: the weak point is how many concepts a department has to learn.** To get a working assistant a
department admin meets department, team, knowledge base, source, pipeline/template, site profile, client/audience,
gateway model, keys, draft/version/publish (for knowledge bases and assistants) and about 20 `Settings.*` sections.
That is close to Eneo's kitchen-sink problem, moved into the admin UI. A department thinks in three things:
**our content, our assistant, where people use it.** Every item below reduces what a department has to know.

## 2. What to do, in order

| # | Item | Phase | Decision status |
|---|---|---|---|
| 1 | **Organisation model** (P1b items 33–38): one umea.se knowledge base owned by the department that runs it, shared with the municipality, topics per assistant link, contact person, teams hidden in forms, the Map | P1b, first | decided (Decisions "Organisation …", #318); not started |
| 2 | **One guided "New assistant" path**: pick our or a shared knowledge base (and topics) or paste URLs, choose a purpose preset, test, publish. Pipelines, site profiles, teams and retrieval settings sit behind "Advanced". The backend exists (preview, test retrieval, publish) | P1b, after item 33 | **changes a decision** (with item 3) |
| 3 | **Three assistant presets** instead of a blank system prompt: "Citizen service" (public, cites umea.se), "Internal policy Q&A" (internal), "Case-handling support" (internal). Each sets prompt, level and model; a department edits the text, not the model choice | P1b, with item 2 | **changes a decision**: "Weakness review 1" moved assistant templates to P2; this takes a fixed set of three into P1b (platform-defined, no template editor) |
| 4 | **Fewer settings for departments**: a department sees schedule, scope, retention and its contact person; the rest of `Settings.Chat`, `Settings.Retrieval`, `Settings.Crawl` stays on the platform-admin pages | P1b, UI only | fits current rows |
| 5 | **Unanswered questions to the content owner**: thumbs-down and "the sources don't say" answers reach the knowledge base's contact person as "questions your content couldn't answer", with "add as test question" (human layer). Uses data already stored (feedback metadata, not-found outcomes) | P1b, after item 33 (contact person) | fits current rows; A1 still limits stored text, so the list shows what the feedback import holds |
| 6 | **"Use this assistant" page** per assistant: OpenAI-client snippet, Knowledge API example, how to request a key | P1b, small | fits current rows |
| 7 | **Count near-duplicate pages as correct answers in the evaluation**: a generated question's expected documents include live documents with the same chunk text (text hash) as its source chunk | P1b, stream A | needs a rule in processing §13.1 |

**After these:** an internal pilot on one department's own documents. Uploads work today; SharePoint is P2 in the
phase plan (`plans/README.md`, "P2 Broaden"), with no connector yet, so a SharePoint pilot is a phase decision.

## 3. Corrections to the first version

- **Per-department model lists (first version, item 6) are not a small API change.** The token Open WebUI forwards
  carries `sub`, `name`, `email` and `role` only (`src/Ume.Rag.Api/OpenWebUiUserJwt.cs:105-113`), no department
  codes. The route is the W3 spike (Open WebUI groups hide models, our API still decides), decided to run now.
- **Automatic `coverageMaxDistance` (first version, item 4) can't come from generated questions alone.** They are all
  answerable, so they can't place the "the sources don't say" cut-off; that needs off-topic questions too, which is the
  gold-set calibration (P1b item 21). Show the outcome there ("answers 94 % of in-scope questions, refuses 6 of 7
  off-topic ones"), not a number to tune.
- **Grouping results by page family (first version, item 8)** is not the first fix: the near-duplicate misses are mainly
  an evaluation artefact (the right content on a sibling page). Item 7 fixes the measurement; grouping can follow if
  citations show many siblings.
- **Access changes are not edited in the assistant's draft.** Audience, retention, history mode and allowed origins are
  on the assistant itself, outside the versioned draft (`src/Ume.Rag.Domain/Entities/AssistantEntities.cs:39-48`), with
  their own route (`PUT /api/assistants/{id}/audiences`) and tab.

## 4. Is "Assistant" the right abstraction?

**Yes, keep the name.** It is the right word for staff and departments and fits the OpenAI-compatible design, where
each assistant appears as a "model". Rejected: "chat with a knowledge base" only (loses personas, combined sources and
versioned prompts), "Agent" or "App" (suggest tools and workflows the platform doesn't do), "Model" as the admin noun
(admins also pick a real model).

**The split already exists in part.** Behaviour and knowledge scope (prompt, model, collections) are versioned; where
it is used (audience, origins, retention) is not. What is missing is **per-channel** settings: one assistant can't have
different retention or limits in Open WebUI and in a widget. That matters once there is a second channel and stored
history (A1). Until then:

- **Placement as its own object: later.** Revisit when a second channel (widget, Teams) is planned; it is a one-step
  change before production (Decisions "No backward compatibility before production").
- **A default "Ask <knowledge base>" assistant per knowledge base: after W3.** Until Open WebUI can show models per
  group, every user would see every default assistant.

## 5. Avoid

Growing the visual pipeline builder beyond fixed templates with parameters; a workspace/project owner type (option C of
the organisation proposal) until a real project asks for it.

## 6. Next steps

1. User: yes or no to items 2–3 (they move assistant templates from P2 into P1b as three fixed presets).
2. Main session: a Decisions row for items 2–7; rules in admin design §5.4 (guided path, presets), §5.7 (department vs
   platform settings), §8 (unanswered questions), processing §13.1 (item 7); P1b items with done-when and streams.
3. Streams: B starts with the organisation schema (item 33), then items 2–6 on top; A takes item 7 and 37–38.
