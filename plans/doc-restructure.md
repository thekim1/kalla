# Design-document restructure (planned, not started)

Owner: main session. Written 2026-10-31 (session 12) after a review of `plans/`. **Do this after milestone 1, at
the P1a/P1b boundary, while stream B is between items.** Stream B and the agent briefs cite these paths and section
numbers, so don't change them in the middle of an item.

## Why

The documents have grown in layers, and finding out how something works now takes five sources:

1. **Precedence chain.** To read one area you need the Decisions table (`01-general-idea.md` §10, 89 rows, "later
   rows win"), then `weakness-review.md`, then `simplification-pass.md` (and `performance-pass.md`,
   `consistency-pass.md`), then the detail design, and then the code. The passes are deltas over the detail documents.
   52 files refer to them. A single section can't be trusted on its own, and an implementor briefed with one section
   gets a partly wrong picture.
2. **Detail documents are turning into changelogs.** 208 "As built (PR #…)" notes and 245 PR references across the
   design documents (connectors 52, engine 44, processing 24, data-model 22, repo-and-deployment 16, knowledge 13).
   Many sentences read "design says X; as built (#108): Y; as built (#155): Z". Every session-end sync adds more.
3. **Detail documents mix four things.** They are 900–1,460 lines each and hold the current spec, rationale and
   alternatives (Eneo/Onyx comparisons), P2/P3 outlines and as-built notes. The text doesn't show which parts are
   binding now.
4. **The Decisions table keeps superseded rows** without a marker (about 6 of 89 say what they replace).
5. **Phase scaffolding piles up.** `plans/p0/` is finished but sits beside `p1/` and `p1b/`. `to-verify.md` mixes
   139 open and resolved rows.

## Hard constraint: stable names and section numbers

About 1,360 code comments in `src/` and `tests/` cite design sections ("processing design §3.7", "data-model §2.1",
"engine §9.6"). The results files and PR bodies cite `to-verify.md` row numbers. So:

- **Never renumber a section or move it to another file.** Move content *within* a section, or to the end of a
  document under a new, higher section number.
- **Keep `to-verify.md` row numbers.**
- Leave a stub (one line with a link) wherever a file moves.

## Steps (in order of value)

1. **Fold the passes into the detail documents, then archive the passes.** For each binding rule in
   `weakness-review.md`, `simplification-pass.md`, `performance-pass.md` and `consistency-pass.md`, write it into the
   detail section it changes. Then move the four files to `plans/archive/`, each with a header: "Folded into the detail
   documents on <date>; kept for the reasoning." Change the precedence in `AGENTS.md`, `HANDOVER.md`,
   `plans/README.md` and the implementor/stream prompts to two layers: **Decisions table > detail document**.
   Needs a Decisions-table row ("Design documents: one layer").
2. **Write as-built notes as present tense.** "As built (PR #155): the scheduler counts from `CreatedAt`" becomes
   "The scheduler counts from `Sources.CreatedAt`." Collect the PR references in a short `## Change log` at the end of
   each document (one line per PR: number, date, sections touched), or leave them to git history. From then on the
   session-end design sync **edits the sentence in place** and doesn't append notes. Update
   `plans/p1/prompts/main-session.md` accordingly.
3. **Separate "later" from "now" inside each document**, without renumbering. A P2+ subsection gets "**Later (P2):**"
   as its first line, or moves into a final "Later phases" section. Alternatives and comparisons with other platforms
   move to a final "Rationale" section, or become a link to `platforms/`.
4. **Give the Decisions table a status.** Either add a column (`current` / `superseded by row N`), or split it into
   "Current decisions" and "History" (keeping the history rows verbatim, since the Architecture Decisions are
   append-only). AGENTS.md's "later rows win" then becomes "the Current section is binding".
5. **Archive finished phases and split `to-verify.md`.** Move `plans/p0/` to `plans/archive/p0/`, with a redirect line
   in `plans/README.md` and `plans/progress.md`. Split `to-verify.md` into open and resolved rows (same numbers), or
   add a status column and sort on it.
6. **Tidy the repo root (optional).** Move `01-general-idea.md` and `02-architecture-review.md` under `plans/`, leaving
   stubs. Only do this if AGENTS.md and the prompts are updated in the same commit.

**Keep as is:** the area folders (`plans/<area>/plan.md` + `design*.md`), `docs/` for operating and `plans/` for
design, `platforms/` as reference, and the `p1`/`p1b` stream layout.

## How

- Main session, through agents, one area per commit (e.g. "connectors: passes folded, present tense, later marked").
  Review each diff. Push after each.
- **Check script** (write it first, in `scripts/`): for every `<doc> §N.N` cited in `src/`, `tests/` and `plans/`,
  check that the heading still exists in that file; also check that every relative Markdown link resolves. Run it
  before and after each commit, and it must show no new misses.
- Tell stream B on GitHub before starting (issue labelled `stream-b`): which files change, that § numbers stay, and
  that the precedence is now two layers.
- Estimate: steps 1, 2 and 4 are about one session of agent work plus review; steps 3, 5 and 6 are cheap follow-ups.

## Done when

- `AGENTS.md`, `HANDOVER.md`, `plans/README.md` and the prompts name two layers of truth.
- No "As built (PR #" left in `plans/**/design*.md`, `data-model.md` or `repo-and-deployment.md`; each has a change log.
- The passes and `plans/p0/` are in `plans/archive/` with stubs or redirects.
- The check script reports no broken § references or links.
