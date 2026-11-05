# Ume.Rag

RAG, crawling and chat platform for Umeå kommun, built as small .NET 10 services on top of the team's LLM gateway.
This repo holds the design (`01-general-idea.md`, `plans/`, `platforms/`) and, from P0, the code in the layout of
`plans/platform/repo-and-deployment.md` §1.

## Where the truth is

Two layers, read in this order:

1. `01-general-idea.md` §10 **Decisions table**. Rows with status `current` bind; a `partly superseded` row binds for
   what its status says; a `superseded` row doesn't bind.
2. The detail document for your area (`plans/*/design*.md`, `plans/platform/data-model.md`,
   `plans/platform/repo-and-deployment.md`). `HANDOVER.md` maps areas to documents. It states every rule that
   applies, in the present tense; where it and the Decisions table differ, the table wins and the document is a bug
   to report.

`plans/weakness-review.md`, `plans/simplification-pass.md`, `plans/consistency-pass.md` and most of
`plans/performance-pass.md` are folded into the detail documents and no longer bind; they stay for the reasoning, each
with a ledger of where every rule went. The capacity model, scaling triggers and scale tests of the performance pass
remain binding as reference (its header says which sections).

Your assignment is in `plans/p0/`:
- the brief for your stream gives your items in order, each item's done-when criterion, and what you hand to the
  other stream;
- your progress file (`progress-a.md` or `progress-b.md`) says where you are. Tick its boxes in the PR that does the
  work.

Phase progress is in `plans/progress.md`. Prompts to start each stream are in `plans/p0/prompts/`. Design sessions
(not stream work) start from `HANDOVER.md`.

The gateway (`~/git/llmgateway`, github.com/thekim1/llmgateway) is the reference implementation. When a design says
**copy**, take the gateway file unchanged. When it says **adapt**, change only what the design lists. Keep the
gateway's conventions for names, config keys, claims, key format and security events.

## Working in a stream

- **Owned paths.** `.github/stream-ownership.txt` assigns every path to stream `a`, stream `b`, `shared` or `main`, and
  CI checks each PR against it by branch prefix. When you need a change in a path you don't own, open a GitHub issue
  labelled `stream-a` or `stream-b` for the owner. Build against the agreed contract in the meantime.
- **Shared files** (`shared` in the map) are append-only. Add your lines in a small PR of their own, and leave the
  ordering and formatting of existing lines as they are.
- **Design documents are read-only for streams.** If the code has to differ from the design, take the smallest
  sound option and list it under "Design deviations" in the PR. The main session settles it in the design.
- **Tests.** Run locally the test projects your change touches, and say which in the PR. CI runs the full backend
  suite; the image tests and the evaluation smoke run when their paths change or before a release, and the browser
  tests (`e2e.yml`) only before a release (repo-and-deployment §8.2). Don't dispatch workflows by hand unless an item's
  done-when asks for it; an item whose done-when needs the browser tests is ticked on the first release run.
- **Results.** Record each check from `plans/to-verify.md` in your stream's results file (`plans/p0/results-a.md` or
  `plans/p0/results-b.md`): row number, result, evidence (command, output, link).
- **PRs.** One PR per checklist item or smaller. Branch `a/<item>-<slug>` or `b/<item>-<slug>`, rebased on `main`,
  `verify.yml` green, squash-merged. The PR body follows the template and shows the done-when evidence.

## Conventions the code won't tell you

- **Settings rule:** deployment knobs are `Section:Key` (configuration, environment). Run-time values are
  `Settings.<Section>.<Key>` (database, changed in the admin UI).
- **7-day package rule:** pin exact versions released at least 7 days ago (NuGet, npm, PyPI). Pin images by digest.
  **Suspended during development** (user, 2026-10-31): until the first production deployment, newer releases may be
  pinned; exact versions and digests are still required, and the rule applies again before go-live.
  Match the gateway's pin when it has one.
- **No backward compatibility before production** (user, 2026-11-01): until the user says explicitly that the platform
  runs in production, any change is allowed. Schemas, migrations, contracts, job payloads, APIs and configuration keys
  may change in one step: no expand/contract steps, no fallbacks for old rows or payloads, no compatibility shims
  (existing ones may go). Rebuild or re-crawl development data instead. The gateway's real API still binds, and the PR
  still lists the change and its hand-offs.
- **Verify before relying:** check a library, API or site claim against the real source. Download packages into a
  scratch directory and read them there without executing them.
- **Language:** code, comments, docs and commits are in English. Admin UI text is English and lives only in
  `labels.ts` and `messages.ts`, so a Swedish translation can be added later (Decisions, "Admin UI in English").
- **Every model call goes through the gateway** with an explicit residency ceiling. Locally and in tests that means
  `Ume.Rag.FakeGateway`.

Build, test and run commands are in `README.md`; operating the platform is in `docs/`.
