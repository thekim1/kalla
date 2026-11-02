# Architecture plans: usefulness and abstractions

Status: proposal, 2026-11-02. Not binding until a Decisions row in `01-general-idea.md` §10 adopts it.
Based on the design documents (`01-general-idea.md`, `HANDOVER.md`, `plans/admin/organisation-proposal.md`,
`plans/demo-path.md`) and a skim of the admin API and admin UI. Nothing was measured.

## 1. Assessment

**Architecture: sound, keep it.** Small services (chat and knowledge already one `Ume.Rag.Api`), the control plane
apart from the request path, every model call through the gateway, Postgres as the only search store, worker and parser
as their own containers. The cost of change is not in the backend.

**Usefulness: the weak point is how many concepts a department has to learn.** To get a working assistant a
department admin meets:

- department, team, knowledge base, source, pipeline/template, site profile;
- client/audience, gateway model, keys, draft/version/publish (for both knowledge bases and assistants);
- around 20 `Settings.*` sections and knobs such as `coverageMaxDistance`.

That is close to Eneo's kitchen-sink problem, moved into the admin UI. A department thinks in three things:
**our content, our assistant, where people use it.**

## 2. Low-hanging fruit, in priority order

1. **Organisation model first (P1b items 33–38).** Already decided: one umea.se knowledge base owned by
   communications, shared with the whole municipality, with topics per assistant link. It removes one crawl per
   department, drifting copies and the question of where shared content lives. Do it before more UI polish on the
   current model.
2. **One "New assistant" path that does everything.** Paste URLs or pick a shared knowledge base and topic, choose a
   purpose preset, test, publish. Pipelines, site profiles, teams and retrieval knobs stay behind "Advanced". Most of
   the backend exists (preview, test retrieval, publish).
3. **Assistant presets instead of blank system prompts.** For example "Citizen service (public, polite, cite
   umea.se)", "Internal policy Q&A", "Case-handling support". Each sets prompt, classification, audience defaults and
   model. Departments don't write prompts or choose models.
4. **Calibrate `coverageMaxDistance` automatically.** The demo needed it moved by hand from 0.50 to 0.55. Set it from
   the generated test questions after each large sync and show the outcome ("answers 94% of in-scope test questions")
   rather than a number to tune.
5. **Feedback to the content owner.** Thumbs-down and "not found" answers go to the knowledge base's contact person as
   "questions your content couldn't answer", with an option to add them to the test set. This keeps content current
   and uses data already stored.
6. **Fix "audience = channel" sooner.** Every assistant on the Open WebUI client is visible to every Open WebUI user.
   If the forwarded Open WebUI JWT carries `departmentCodes`, filtering `/v1/models` by department is a small API
   change that doesn't wait for W3/E1. Check what the token contains first.
7. **A "Use this assistant" page per assistant.** OpenAI-client snippet, Knowledge API curl example, widget embed code,
   how to request a key. API-first only pays off if integrating teams find this without asking.
8. **Group near-duplicate results by page family.** The remaining test-set misses are mostly near-duplicate school
   pages. Limiting results per URL template or page family is a cheap retrieval fix and gives cleaner citations.
9. **Fewer settings.** Sensible platform defaults; expose only what a department changes (schedule, scope, retention).
   Move the rest of `Settings.Chat`, `Settings.Retrieval` and `Settings.Crawl` to a platform-admin page.

**Biggest usefulness gap:** the demo covers only public umea.se content. Most internal value is in SharePoint and
uploaded files (policies, routines, guidelines). After items 1–3, run one internal pilot on a department's SharePoint
library.

**Avoid:** growing the visual pipeline builder beyond fixed templates with parameters, and a workspace/project owner
type (option C of the organisation proposal) until a real project asks for it.

## 3. Is "Assistant" the right abstraction?

**Yes, keep the name.** It is the right word for staff and departments and fits the OpenAI-compatible design, where
each assistant appears as a "model". The problem is that the object bundles three things:

| Part | Contents | Who cares about it |
|---|---|---|
| Behaviour | system prompt, model, tone, classification | the department writing it |
| Knowledge scope | linked knowledge bases (and topics), retrieval settings | the content owner |
| Placement | audience/client, allowed origins, keys, retention, rate limits | whoever runs the channel |

Consequences:

- One assistant can't have different retention or rate limits in Open WebUI and in the public widget; copies drift.
- Changing who can use it is edited in the same draft as the prompt, so access changes look like behaviour changes.
- Simple cases ("let staff ask our policy knowledge base") still need a whole assistant with a prompt.

### Proposed shape

1. **Split placement into its own object** ("Where it's used" in the UI; `Placement` or `Deployment` in the model).
   It links one assistant to one channel (Open WebUI, widget, integration key, later Teams) and carries that channel's
   audience, origins, retention and rate limit. The assistant keeps behaviour and knowledge scope and is versioned and
   published as today; placements change without a new assistant version. Before production this is a one-step change
   (Decisions, "No backward compatibility before production"). It is also the home for item 6 and item 7 above.
2. **A default assistant per knowledge base.** Each knowledge base (and each topic of a shared one) gets an
   "Ask <knowledge base>" assistant from a standard preset. Departments build their own assistant only for a persona,
   a combination of sources or special rules.

### Rejected alternatives

- **No assistants, only "chat with a knowledge base":** simpler, but loses personas, combined sources,
  public/internal variants and versioned prompts.
- **"Agent" or "App":** suggests tools and workflows the platform deliberately doesn't do; invites scope creep.
- **"Model" as the user-facing noun:** correct in the API, confusing in the admin UI where admins also pick a real
  model.

## 4. Next steps

- Decisions row for the assistant/placement split and default assistants per knowledge base.
- Data-model change: assistant ↔ placement.
- P1b items or issues for §2 items 2–6, each with a done-when criterion.
