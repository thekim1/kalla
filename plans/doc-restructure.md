# Design-document restructure

Owner: main session. Written 2026-10-31 (session 12); revised and started 2026-11-01 (session 16, user: "do the
improvements to the plan and then execute it"). Done at the P1a/P1b boundary while stream B is idle and stream A waits
on B's #239/#241. Stream B and the agent briefs cite these paths and section numbers, so names and numbers stay.

## Why

The documents have grown in layers, and finding out how something works takes five sources:

1. **Precedence chain.** Decisions table (`01-general-idea.md` §10, 94 rows, "later rows win") → `weakness-review.md`
   → `simplification-pass.md` (and `performance-pass.md`, `consistency-pass.md`) → the detail design → the code. The
   passes are deltas over the detail documents, so a single section can't be trusted on its own, and an implementor
   briefed with one section gets a partly wrong picture.
2. **Detail documents are turning into changelogs.** 139 "As built" notes and 438 PR references in the design
   documents (connectors 31, engine 28, processing 18, data-model 15, knowledge 12, repo-and-deployment 10, admin 8,
   chat 7). Every session-end sync adds more.
3. **Detail documents mix four things**: the current spec, rationale and alternatives, P2/P3 outlines and as-built
   notes. The text doesn't show which parts bind now.
4. **The Decisions table keeps superseded rows** without a marker.
5. **Phase scaffolding piles up.** `plans/p0/` is finished; `to-verify.md` mixes open and resolved rows.

## Hard constraint: stable names and section numbers

About 4,100 named citations ("engine design §9.6", "data-model §2.1"), 600 path-form citations and 800 relative links
point into these documents from code, tests, plans and docs; results files and PR bodies cite `to-verify.md` rows.

- **Never renumber a section, rename a linked heading, or move a file.** Move content *within* a section, or add it
  at the end under a new, higher number.
- **Keep `to-verify.md` row numbers and Decisions-table row positions.**
- Retired documents stay where they are, with a header that says they no longer bind (archive in place, no stubs).

## Guard: `scripts/check-doc-refs.py`

Written first (2026-11-01). For every named or path-form `§N.N` citation in `src/`, `tests/`, `plans/`, `docs/`,
`scripts/`, `deploy/`, `contracts/`, `.github/` and the root `*.md`, it checks that the document has that section (a
numbered heading, or a numbered list item or table row under it). For every relative Markdown link it checks the file
and the `#fragment`. It also reports duplicate section numbers. Baseline at the start: 5,569 references, 1 miss (two
`§2.1` headings in `gateway-changes/design.md`). Negative proof: renumbering `engine §9.6` gives 16 misses, renaming
one weakness-review heading 23.

It runs before and after every restructure commit (no new misses), and stays in CI afterwards (the `changes` set of
`verify.yml`) so broken references can't come back.

## Steps

0. **Fold the outstanding deviations first** (not yet in the design): #217's fired-types list in the PII report,
   `UrlCanonicalizer` staying in the worker (#178), the admin routes `/api/organisation/departments` with nested teams,
   and the deviations of #228/#239/#241 once merged. Otherwise they come back as new "as built" notes.
1. **Fold the passes into the detail documents, with a rule ledger.** For each of `weakness-review.md`,
   `simplification-pass.md`, `performance-pass.md` and `consistency-pass.md`, list every binding rule with its target
   section and a status: *folded* (written into the target section), *already there*, *obsolete* (superseded by a later
   Decisions row, named) or *not a rule* (rationale, history). Open risks and unresolved findings are not rules: they
   go to `postponed-decisions.md` or `to-verify.md`, so they survive the archiving. Review against the ledger, not
   only the diff. Then give each pass a header: "Folded into the detail documents on <date> (ledger below); kept for
   the reasoning; not binding." The ledger goes at the end of the pass.
   - `performance-pass.md` is also the only home of the capacity model, topologies, scaling triggers and scale tests
     (§2, §5–§7). Those sections stay binding as the reference for capacity; only its overrides (§1, §3, §9) are folded.
     Its header says which is which.
   - Precedence becomes two layers: **Decisions table > detail document**, in `AGENTS.md`, `HANDOVER.md`,
     `plans/README.md`, the stream prompts and `plans/p1/prompts/implementor-common.md`. Decisions row "Design
     documents: one layer".
2. **Present tense, no PR history in design text.** "As built (PR #155): the scheduler counts from `CreatedAt`"
   becomes "The scheduler counts from `Sources.CreatedAt`." References to merged PRs go; git history (`git log -L`,
   `blame`) keeps them, so no change-log sections. Links to open issues and hand-offs (work still to do) stay. The session-end design sync edits the sentence in place from then on
   (`plans/p1/prompts/main-session.md`).
3. **Mark what is later.** Whole documents that are P2/P3 (`tools/`, `workspaces/`) get one line at the top. In mixed
   documents a P2+ subsection starts with "**Later (P2):**". Alternatives and comparisons with other platforms move to
   the end of their section or become a link to `platforms/`.
4. **Decisions table status column**: `current`, or `superseded by "<Topic>"` / `partly superseded by "<Topic>": <what holds>` (rows are named by Topic). Rows keep
   their place. "Later rows win" becomes "rows marked current bind".
5. **Retire finished phase material in place.** `plans/p0/README.md` gets a "Finished (v0.1.0, 2026-10-31); kept for
   the record" header; `plans/README.md` lists it under "Finished". `to-verify.md` gets a status column (rows keep
   their numbers and order).

Dropped from the first version: moving the passes and `plans/p0/` to `plans/archive/` (breaks ~70 links and stream
B's owned paths for no reading gain), per-document change logs (git has them), moving the root documents.

## How

- Main session, with agents: one area per agent on disjoint files, each reading all four passes for its own
  documents and returning its part of the ledger. Areas: connectors; engine; processing; platform (data-model,
  repo-and-deployment, plan); knowledge + chat; admin + identity; parser + tools + gateway-changes + workspaces.
- One commit per area, reviewed against its ledger and checked with `check-doc-refs.py`, pushed after each.
- Stream B is told first (issue labelled `stream-b`): which files change, that § numbers stay, the two-layer
  precedence.

## Done when

- `AGENTS.md`, `HANDOVER.md`, `plans/README.md` and the prompts name two layers of truth.
- Each pass has its header and complete ledger; no ledger row without a status.
- No "As built" note and no reference to a merged PR in `plans/**/design*.md`, `data-model.md` or
  `repo-and-deployment.md`.
- The Decisions table has a status on every row; `to-verify.md` has a status on every row.
- `check-doc-refs.py` reports no misses and runs in `verify.yml`.
