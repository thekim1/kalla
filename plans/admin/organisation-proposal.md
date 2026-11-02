# Organisation, ownership and sharing: proposal

**Status: proposal (2026-11-02), not binding.** It binds once its Decisions row is added; the open questions in §5 go to
the user first. Until then the admin design and the data model describe the platform.

## 1. The model as built

| Object | Owner | Belongs to | Who sees and edits it |
|---|---|---|---|
| Department | gateway catalogue (synced, read-only here) | – | scope comes from `departmentCodes` = `CostCenterCode`, or every department for a platform admin (`src/Ume.Rag.AdminApi/AdminContext.cs:67-77`); member groups are entered by hand (`OrganisationEndpoints.cs:25-35`) |
| Team | gateway catalogue | one department (`Domain/Entities/ConfigEntities.cs:60-70`) | grants nothing in P1 (`plans/identity/design.md` §1.3); used for key attribution and the optional `OwnerTeamId` |
| Collection (knowledge base) | one department, optional owner team (`CollectionEntities.cs:184-205`) | – | department-scoped, 404 outside it (`CollectionEndpoints.cs:198-203`) |
| Source | none of its own | exactly one collection, for life (`CollectionEntities.cs:227-232`) | through its collection |
| Pipeline | platform, or a department copy made on the first edit (copy on write, `PipelineEndpoints.cs`) | – | hidden behind the source's advanced settings |
| Assistant | one department, optional owner team (`AssistantEntities.cs:22-56`) | links ≤ 20 collections **of its own department** (V11, `AssistantEndpoints.cs:528-546`) | department-scoped |
| Audience | the assistant | P1: `client` rows only (`AssistantEntities.cs:112-118`) | every Open WebUI user of that client sees the assistant (identity §5.4) |
| Client (channel) | platform | – | platform admins (generic forms) |

Designed but not built: share lists `CollectionShares` (P2, data-model §2.1; register O2 kept them out of P1b), department and
group audiences (P2, need E1 and W3, `plans/postponed-decisions.md` W3), owner-team editing `EditOwnerTeamOnly` (P2,
admin §3.2), the department map Karta (P2, admin §5.5.6).

### Pain points for a municipality

1. **Municipality-wide content has no home.** umea.se is run by the communications department, but every department's
   assistant needs parts of it. Today each department needs its own crawl source in its own collection (V11 refuses a
   foreign collection). A page then becomes one `Documents` row per source (data-model §2.2 `Documents`). The embedding
   cache saves the embedding cost (`EmbeddingCache`), but the crawl, storage and deletion decisions are repeated and the
   copies drift apart.
2. **Slices of a shared site are awkward.** "Only the building-permit pages" means a separate crawl with its own include
   rules. Path rules that add tags already exist in the chunker (`Worker/Processing/Chunking/Metadata.cs:47-101`), and
   retrieval filters on tags (knowledge design §3, `filters.tags`). The admin UI uses neither.
3. **Nobody can see who owns what.** The Organisation page lists departments and team names only
   (`views/OrganisationView.vue:61-82`). There is no view of department → knowledge bases → assistants → audiences
   until the P2 map, and collections have no contact person (assistants do, `AssistantEntities.cs:30-35`).
4. **Teams are visible but do nothing.** Create forms ask for an owner team that grants nothing in P1. Team codes may
   never reach the token (identity §16, "New (P2)" `teamCodes`).
5. **"Who can use this assistant" is really "which channel".** Every assistant on the Open WebUI client reaches every
   Open WebUI user. A department-only assistant needs its own Open WebUI instance until W3/E1 are solved (W3's interim).
6. **Kommunala bolag** fit only as gateway departments. Whether the gateway's catalogue holds them, and whether sharing
   "with everyone" should include them, is not stated anywhere.
7. **Navigation is by object type** (`router/nav.ts`: Overview, Knowledge bases, Assistants, Platform). It is not
   organised by the questions people ask ("what does my department have", "what can I reuse").

## 2. Options

**A. As designed, faster.** Pull the P2 share lists (named departments) and the map forward; change nothing else.
Small, but shared umea.se content still needs one crawl per slice (pain 2), and "share with everyone" means listing
every department by hand.

**B. Department-owned, municipality-shared, with topics (recommended).**
- **Departments stay the owner and the admin boundary.** No new entity; scope still comes from sign-in. Content used
  across the municipality is owned by the department that runs it (communications owns umea.se; DoIT for the pilot).
  The platform team owns no content.
- **Sharing on the knowledge base:** *Only our department* (default), *Selected departments*, or *Whole municipality*.
  Shared means "usable in other departments' assistants": read-only, no sources or settings visible, no publishing.
- **Topics:** a knowledge base can define topics from URL path rules (the existing tag rules, shown as "Topics"). An
  assistant links a knowledge base **with optional topics**, so one umea.se crawl serves every department's
  assistant ("umea.se › Building and housing").
- **Responsible person on the knowledge base** (name and e-mail, like the assistant's contact). Teams are hidden in the
  UI unless the department turns on owner-team editing (P2).
- **Audience in plain words:** "Who can use it" = channel (Open WebUI pilot, integration, …), and later people
  (departments, groups) once W3/E1 allow it. Until then the page says what the channel means ("everyone who can sign in
  to Open WebUI (pilot)").

**C. Workspaces/projects as a new owner type** next to departments, with members from several departments. This is the
most flexible for cross-department projects, but it is a second ownership model, needs its own membership source
(groups), and repeats what sharing plus department ownership already gives. Revisit when a real project asks for it.

### What B changes

| Area | Change |
|---|---|
| Data model | `Collections.SharedWith` (`department`/`selected`/`municipality`) + `CollectionShares` (built now); `Collections.ContactName`, `ContactEmail`; `AssistantVersions.CollectionIds uuid[]` → `CollectionLinks jsonb` `[{ collectionId, tags[] }]` (≤ 20 links, ≤ 20 tags each). One migration; no compatibility steps (Decisions, "No backward compatibility before production") |
| Retrieval | `SearchScope` carries a tag filter **per collection** (today `filters.tags` applies to the whole request) |
| Admin API | V11: a link is valid if the collection is the assistant's department's, shared with it, or shared with the municipality; `PUT /api/collections/{id}/sharing` (`Manage`, audited `collection.share`); `GET /api/collections?view=mine\|shared\|all`; `GET /api/collections/{id}/topics` (tags with document counts); `GET /api/organisation/departments/{id}/map` (table first); `GET /api/departments` naming fixed (design says `/api/departments`, code has `/api/organisation/departments`: keep the code's and fix the design) |
| Roles | unchanged. Sharing is `Manage` (department admin). Unsharing a knowledge base that other departments' published assistants link is refused with their names (new V-rule) |
| Worker | none for sharing. Topic rules are the existing path-rule parameter of the templates (`x-labels` in English) |
| Decisions | new row "Organisation: department-owned, municipality-shared knowledge bases with topics". It **conflicts with current rows**: P1b briefs O2 ("share lists stay P2") and Weakness review 1 (Karta moved to P2). That is the user's call (§5, Q1–Q2) |

## 3. UI

### Navigation (English, gateway side menu, filtered by role)

```
HOME
  Home                  my department: setup checklist, needs attention, at a glance
  Map                   who owns what: departments → knowledge bases → assistants → channels
CONTENT
  Knowledge bases       tabs: Ours · Shared with us · All in the municipality
ASSISTANTS
  Assistants
FOLLOW-UP
  Feedback
ADMINISTRATION
  Organisation          departments (admins, member groups, sharing in/out), teams
  Platform              clients, keys, settings, data protection, site profiles, models (generic forms)
  Audit log
Header: Department: Building & Environment ▾ (all for multi-department users) · search (Ctrl K)
```

### Main journeys

1. **Set up a department's assistant:** Home → checklist → Create knowledge base → Add source → Preview → Publish →
   Create assistant → Test → Publish → Who can use it. This is the existing §5.4 journey; the checklist now also offers
   "Use shared content instead" when a shared knowledge base already covers the site.
2. **Reuse municipal content:** Assistant → Knowledge → **Add knowledge base** → picker with Ours / Shared /
   Municipality → choose umea.se → tick topics → shows the document count per topic.
3. **Share our content:** Knowledge base → Sharing → choose who → the list of assistants that use it, per department.
4. **Find who owns what:** Map, or the "Responsible" line on every knowledge base and assistant.

### Wireframes

**Home (department view)**
```
Home                                                          Department: Building & Environment ▾
┌ Get started: Building permits ───────────────┐ ┌ Needs attention (2) ─────────────────────────────────┐
│ ✓ Knowledge base  ✓ Source  ◐ First fetch    │ │ ⚠ 212 documents await a deletion decision  [Review]  │
│ ○ Publish  ○ Assistant  ○ Make available     │ │ ⚠ Evaluation dropped 6 points: Permits      [View]   │
│ Tip: umea.se is already shared by Communications – [Use shared content]                               │
└──────────────────────────────────────────────┘ └──────────────────────────────────────────────────────┘
┌ At a glance ────────────────────────────────────────────────────────────────────────────────────────────┐
│ Knowledge bases 3 (1 shared with us) · Assistants 2 (1 published) · Questions last 7 days 1 240        │
│ Responsible: Anna Andersson (department admin) · Team Permits                                          │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

**Knowledge base › Sharing and topics**
```
Knowledge bases › umea.se (Communications)                 [Public] [Published]        [Publish] [⋯]
Overview  Sources (1)  Documents (2 323)  Topics (9)  Quality  Search settings  Sharing
┌ Sharing ───────────────────────────────────────────────────────────────────────────────────────────────┐
│ Who may use this knowledge base in their assistants?                                                   │
│ ( ) Only Communications   ( ) Selected departments [ … ]   (•) Whole municipality                      │
│ Shared use is read-only: other departments see the content in their assistants, not the sources.     │
│ Used by 4 assistants in 3 departments                                                                  │
│ │ Assistant            │ Department              │ Topics                  │ Status     │             │
│ │ Permits assistant    │ Building & Environment  │ Building and housing    │ Published  │             │
│ │ Customer service     │ Contact centre          │ all                     │ Draft      │             │
│                                                                                   [Save sharing]       │
└────────────────────────────────────────────────────────────────────────────────────────────────────────┘
Topics: rules on the URL path, set on the source (Processing › Topics)
│ Topic                │ Rule                               │ Documents │
│ Building and housing │ www.umea.se/byggaboochmiljo/**     │ 412       │
│ Politics             │ www.umea.se/kommunochpolitik/**    │ 655       │
```

**Assistant › Knowledge and Who can use it**
```
Assistants › Permits assistant  (model name: bygglov)        [Internal] [Published: version 3]
Draft  Test  Versions  Who can use it  Retention  Statistics
Knowledge                                                                  [Add knowledge base]
│ Permits forms (ours)                       all topics                          [Remove]            │
│ umea.se (Communications, shared)           Building and housing ×  [+ topic]   [Remove]            │
┌ Add knowledge base ─────────────────────────────────────────────────────────────────────────────────┐
│ Ours · Shared with us · Whole municipality          Search [            ]                          │
│ ○ umea.se — Communications — Public — 2 323 documents — 9 topics                                   │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
Who can use it
│ Channel: Open WebUI (pilot) — everyone who can sign in to it                       [Change]        │
│ People: departments and groups come when Open WebUI can tell us who the user is (planned).         │
```

**Map (table first, with a drawing above it)**
```
Map                                          Department: All ▾   Show: Everything ▾   [Table] [Drawing]
│ Department      │ Knowledge base    │ Shared        │ Assistant           │ Channel           │ Responsible       │
│ Communications  │ umea.se           │ municipality  │ –                   │ –                 │ K. Lund           │
│ Building & Env. │ umea.se ↗ (shared)│ –             │ Permits assistant   │ Open WebUI (pilot)│ A. Andersson      │
│ Building & Env. │ Permits forms     │ –             │ Permits assistant   │ Open WebUI (pilot)│ A. Andersson      │
│ DoIT            │ Turkos            │ –             │ turkos              │ Open WebUI (pilot)│ demo              │
```

Same design system and components as now (`DataTable`, `UiTabs`, drawers, `SegmentedControl`); the drawing is plain SVG
over the same rows (admin §5.5.6); no new library.

## 4. Plan

| # | Stream | Item | Done when |
|---|---|---|---|
| 0 | main | Decisions row; data-model §2.1 (`SharedWith`, contact, `CollectionLinks`), admin §3.2 (sharing in P1b), §4.5 (routes), §4.6 (V11 widened, new unshare rule), §5.2–5.5 (nav, Home, Map, Sharing, Topics, Who can use it), knowledge §3 (per-collection tags), register O2 closed, Karta moved to P1b | `check-doc-refs.py` 0 misses; issues to A and B |
| 1 | B | Schema: `Collections.SharedWith`, `ContactName/Email`, `CollectionShares`; `AssistantVersions.CollectionLinks`; migration rebuilds dev data | migrations run; `PublishedAssistantVersions` exposes the links |
| 2 | B | Retrieval: per-collection tag filter in `SearchScope`; chat and the test panel pass the links | retrieval test: shared collection + topic returns only tagged chunks; untagged link = whole collection |
| 3 | B | Admin API: sharing endpoint, V11 widened, unshare refusal, `view=` listing, topics endpoint, map endpoint | `PermissionMatrixTests`; another department sees a shared base's name and topics but not its sources (404) |
| 4 | A | Evaluator and test-question generation follow the links (worker uses `Ume.Rag.Retrieval`); topic rules are an English-labelled template parameter shown in preview | evaluation of an assistant with a topic link scores only that topic's questions; preview shows topics per document |
| 5 | B | UI: nav regrouped (Home, Map, Knowledge bases with three tabs, Organisation, Platform), Home per department, Sharing and Topics tabs, link picker with topics, "Who can use it", Map table + SVG | vitest for each page; the journeys of §3 run by keyboard in the e2e fixture |
| 6 | B | Hide teams in create forms (owner team set from the creator's first team, editable under ⋯) | create drawers have no team field |
| 7 | A | `platform/**`: Organisation generic form shows member groups and sharing summary; English keywords | spec updated |
| 8 | later | People audiences (departments, groups) after the W3 spike and E1 | – |

Hand-offs: 1 → 2, 3 (B internal); 1 → 4 (schema, `CollectionLinks`); 3 → 5; 4 needs 2's `SearchScope`. Order after
#314 (English UI) lands, so the new pages are written in English once.

## 5. Open questions (with recommendations)

1. **Share lists in P1b instead of P2** (conflicts with register O2 and the P1b briefs row). *Recommended: yes*; the
   umea.se case is the pilot's own content model.
2. **The map in P1b, table-first** (conflicts with Weakness review 1, which moved Karta to P2). *Recommended: yes,
   table only; the drawing can follow.*
3. **Who owns municipality-wide content?** *Recommended: the department that runs it* (communications for umea.se;
   DoIT until communications has admins), not the platform team.
4. **Does "whole municipality" include kommunala bolag?** *Recommended: it means every active department in the
   gateway catalogue; if bolag are listed there they are included, otherwise share with them explicitly once they
   exist.* Needs a check of the gateway catalogue.
5. **Per-group assistants in one Open WebUI before E1:** *Recommended: run the W3 spike now (Open WebUI group
   permissions hide models; our API still decides) and keep one Open WebUI per department as the fallback.*
6. **Hide teams in the UI:** *Recommended: yes*, until a department turns on owner-team editing.
