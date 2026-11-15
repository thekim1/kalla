# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.6.0`** (2026-11-15); the user's VM runs 0.5.0 and the user is to upgrade it to 0.6.0. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every
commit; HTTPS through `gh auth git-credential`, handbook *Environment notes*).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. **[`plans/roadmap.md`](plans/roadmap.md): the plan ahead.** Done by release, the critical path to go-live with each
   step's input and owner, work that needs no input, and the open calls. HANDOVER doesn't repeat it.
2. [`plans/decisions.md`](plans/decisions.md): the decisions in force; every entry binds. Record a decision by changing
   or adding an entry, then a dated line under *Later decisions* in `plans/archive/decisions-log.md`.
3. **[`plans/work/main-handbook.md`](plans/work/main-handbook.md): how we work (the user's preferences, binding), the
   local stack, lessons, environment notes.** Read it at the start of a session; add a lesson there when you learn one.
4. `plans/work/`: the queues `stream-a.md`, `stream-b.md`, `main.md` (open items only; finished ones are in
   `plans/archive/work/`), results in `results-a.md` and `results-b.md`.
5. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and doesn't bind.

## Start here (session 40)

Nothing is in flight: no open PRs, no worktrees, `main` at `7d6e636f`. Ask the user what comes next. Candidates:

1. **The user's upgrade to 0.6.0** (`docs/portainer.md`, *Upgrade from 0.5.x to 0.6.0*): help if asked. Before it,
   rag-api's and rag-admin's credential keys get `OnPrem` and `Eu`, and each published assistant needs a gateway key
   that reaches both its chat model and its embedding model. Pulls that say "denied": `docker logout ghcr.io`.
2. **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
   F3, F4, F8 and F10.
3. **0.6.1:** C13 (the M-16 session, #667) is free to go; the release files are free again (posted on #667).
4. **The next release run checks three things** for the first time (note them in that session):
   - `:latest` moves in `publish` (#756);
   - verify and parser run in full on a tag, a moved one too (#755): admin-ui, images, renderer and the parser jobs run;
   - the tag run takes about 17 min (0.6.0: 27).
5. **For the user:** *Allow GitHub Actions to create and approve pull requests* is still off (Settings → Actions →
   General), so `release.yml` couldn't open the pin PR; it was opened by hand (#754).

## State

- **Release:** `v0.6.0` published 2026-11-15 ([run 43720085132](https://github.com/thekim1/ume-rag-platform/actions/runs/43720085132)),
  tag at `d5029e29`; `deploy/portainer/` is pinned to its images (#754), with the gateway's v0.10.3 (#747, which holds
  llmgateway#96).
- **Release speed (#756), stopped there by the user:** build runs beside verify and parser, publish waits for all,
  e2e (when asked) runs after build, the non-.NET SBOMs run in the background. Dry run 13 min 26 s (was about 20½).
  Not done, and why:
  - caching the parser's models for tag runs conflicts with #704 (PR jobs share the runner user); with the release
    runner of its own before go-live (roadmap step 7);
  - smoke test and Portainer boot in parallel (about 2 min): two full stacks at once on a host whose memory we can't see.
- **Fast checks of the stack:** `gh workflow run release.yml --ref <branch> -f portainer_boot=X.Y.Z` boots `<branch>`'s
  Portainer stack against X.Y.Z's images on ghcr.io in about 2 min (#752; handbook, *Release dry runs*).
- **Open proposals for the user:** `plans/proposals/support-chatbot-features.md` (above).
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.
- **Tools here:** `actionlint` is installed (`/snap/bin/actionlint`); `shellcheck` is not; `grep` is ugrep (handbook,
  *Environment notes*). The local `gh` token can't read ghcr packages: live stack tests run on the runner.

## Session 39 (2026-11-15)

- **0.6.0 published:**
  - #751: the Portainer boot test waits (`--wait`) only on the long-running services; a one-shot that exits 0 with
    nothing waiting for it failed `up --wait`.
  - #752: the boot-only manual run; it verified #751 against the `0.6.0` images in about 2 min.
  - The tag moved to `d5029e29`; the run published; #754 (the pin PR, opened by hand).
- **#755:** verify.yml and parser.yml run in full for a tag. A moved tag's push has the old tag as `before`, which
  filtered 0.6.0's second run (admin-ui, images, renderer and the parser jobs skipped; covered by the first run).
- **#756:** the release-speed work (above).
- **#753:** handbook and repo-and-deployment §7.1 notes (the boot-only run, the `--wait` lesson, actionlint, ugrep).
- **GitHub:** answered #667's open points (`pilot.spec.ts` fixed in #748; v0.10.3 holds llmgateway#96).
