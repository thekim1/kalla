# Ume.Rag

RAG, crawling and chat platform for Umeå kommun, built as small .NET 10 services on top of the team's LLM gateway.
This repo holds the design (`01-general-idea.md`, `plans/`; background research on other platforms, not binding, in
`platforms/`) and, from P0, the code in the layout of
`plans/platform/repo-and-deployment.md` §1.

## Where the truth is

Two layers, read in this order:

1. `01-general-idea.md` §10 **Decisions table**. Rows with status `current` bind; a `partly superseded` row binds for
   what its status says; a `superseded` row doesn't bind.
2. The detail document for your area (`plans/*/design*.md`, `plans/platform/data-model.md`,
   `plans/platform/repo-and-deployment.md`). `plans/README.md` maps areas to documents. It states every rule that
   applies, in the present tense; where it and the Decisions table differ, the table wins and the document is a bug
   to report.

The cross-cutting passes and the finished phase plans are in `plans/archive/` and no longer bind (each pass ends with a
ledger of where its rules went), except the performance pass's capacity model, scaling triggers and scale tests, which
remain binding as reference (`plans/roadmap.md` names the sections).

**Your assignment** is your stream's queue in `plans/work/` (`stream-a.md` or `stream-b.md`): take the first item that
is not ticked and not waiting, and tick it in the PR that does the work. Each item names its paths, done-when, what it
waits on and the design sections to read. Where the project stands: `plans/roadmap.md`. Start prompts:
`plans/work/prompts/`. Design sessions (not stream work) start from `HANDOVER.md`.

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
  tests (`e2e.yml`) only by hand or before a release when asked (repo-and-deployment §8.2). Don't dispatch workflows by hand
  unless an item's done-when asks for it; an item whose done-when needs the browser tests is ticked on a green run.
- **Results.** Record each check from `plans/to-verify.md` in your stream's results file (`plans/work/results-a.md` or
  `plans/work/results-b.md`): row number, result, evidence (command, output, link).
- **PRs.** One PR per queue item or smaller. Branch `a/<id>-<slug>` or `b/<id>-<slug>`, rebased on `main`,
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
