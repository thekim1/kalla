# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.5.1`** (2026-11-13); **0.6.0 is tagged but not published** (below); the user's VM runs 0.5.0 and the
user wants to deploy 0.6.0 as soon as it is out. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
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

## Start here (session 39)

**Goal: get 0.6.0 published, fast.** The user wants to deploy it and doesn't want to wait 30 minutes per attempt.

1. **Fix the Portainer boot test** (`deploy/tests/portainer-boot.sh:145`). The last dry run, [run
   43716580163](https://github.com/thekim1/ume-rag-platform/actions/runs/43716580163) on `main` at `bea23714`, booted
   everything healthy and `rag-openwebui-setup` minted the feedback key, then `docker compose up -d --wait` failed with
   `container …-rag-openwebui-setup-1 exited (0)`. `--wait` treats a one-shot that exits as a failure, even with code 0,
   unless another service waits for it with `service_completed_successfully`, and nothing waits for this one.
   Portainer doesn't use `--wait`, so a real deploy is not affected: only the test.
   - Fix suggestion: start everything with `up -d`, then `up -d --wait --no-recreate` with every service except
     `rag-openwebui-setup` (`dc config --services`), then wait for the setup job to exit 0 (`docker wait`) and keep the
     existing check of its key.
2. **Get to green faster, without the 30-minute loop** (the user's ask):
   - Don't use a full release dry run to test this fix. Run `deploy/tests/portainer-boot.sh` against the release's own
     images. The `v0.6.0` run pushed the images (it failed after the images step), or build the file with
     `build-stack.sh --images` from a dry run's `images.env`. The script runs on the runner or on this machine, which
     needs `read:packages` for ghcr (the local `gh` token lacks it: ask the user, or run it on the runner with
     `workflow_dispatch` of a small job).
   - Or add a `workflow_dispatch` input to `release.yml` that runs only the Portainer boot against given images.
3. **Then publish:** move the tag (`git tag -f -a v0.6.0 -m "Ume.Rag 0.6.0" <sha> && git push -f origin v0.6.0`); the
   tag run publishes. The tag currently points at `59bd9b71`, which has the setup bug fixed by #749. Moving it is safe
   because nothing was published. The CHANGELOG's 0.6.0 section and the upgrade guide are current through #750.
   - Check the pin PR that `release.yml` opens (it needs *Allow GitHub Actions to create and approve pull requests*;
     the user was asked to check that setting).
   - Post on #667 when the release is out (the M-16 session stays off release files until then).
4. **The release-speed work** (the user decided it comes next, after 0.6.0). From the last runs: `verify` about 6 min
   (backend tests), then `build` about 17 min plus the Portainer boot (parser image 4 min 15 s, renderer 2 min 34 s,
   .NET images 2 min 21 s, other SBOMs 2 min 8 s, bundle smoke 2 min 26 s), all on shared self-hosted runners with no
   shared build cache for tag releases (#704). Ideas to weigh, each with #704 in mind:
   - Run `build` in parallel with `verify` and gate only `publish` on both (about 6 min less).
   - Cache the parser's models (the biggest image) in a cache only tag runs can write.
   - Let a dry run skip what didn't change, or run the two boot tests in parallel.
   - Make each boot test runnable alone (item 2).

## State

- **Release:** `v0.5.1` is the last published (2026-11-13). The user's VM runs **0.5.0** with the Portainer stack,
  `openwebui` and `clamav` on, and an Open WebUI client made by hand. **0.6.0** is tagged at `59bd9b71` but its run
  failed at the Portainer boot; `main` has the fix (#749), the gateway pin v0.10.3 (#747) and the notes (#750).
  - The user's upgrade path: `docs/portainer.md`, *Upgrade from 0.5.x to 0.6.0* (#746), step by step.
  - Before the update: rag-api's and rag-admin's credential keys get `OnPrem` and `Eu`. Each published assistant needs
    an AI gateway key that reaches both its chat model and its embedding model.
  - If pulls say "denied", run `docker logout ghcr.io` (a stale login).
- **The 2026-11-14 review (#694) is closed:** all 18 issues fixed this session (#728–#744). M-17: C0–C12 merged, C13
  left (0.6.1, the M-16 session).
- **Gateway:** v0.10.3 is pinned (#747); it ships our preset change (thekim1/llmgateway#95).
- **Browser tests pass again** (#748, all nine). They hadn't run since 2026-11-08; the fixes included two product bugs:
  - the `gateway_key_required` text read like a cost note, so a refused publish looked like nothing happened;
  - target sizes under 24 px on the audit log and *Used by*.
- **Open proposals for the user:** `plans/proposals/support-chatbot-features.md` (ten features beyond Eneo, from
  `platforms/eneo-vs-ume-rag.md`). My suggestion: F3, F4, F8 and F10 first. Nothing is built until the user picks.
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`. Its HTML sources were in the session
  scratchpad. The user hasn't said whether to keep it in the repo.
- **Nothing runs locally;** no worktrees are left. The user declined installing pwsh and the Aspire CLI for local
  browser tests (2026-11-15).

## Session 38 (2026-11-15)

- **GitHub:**
  - Reviewed and merged #727 (C7), #731 (C12) and #740 (C11).
  - #740 replaced #732, which GitHub closed when C12's branch was deleted (the stacked-PR lesson, now a pre-merge check
    in the handbook). The M-16 session's duplicate, #742, is closed.
  - Merged #747 (gateway v0.10.3) after rebasing it.
- **Fixed the whole #694 review:**
  - #697: `portainer-rerun.sh`.
  - #696/#700: no gateway call under the config lock, and a client change re-checks its assistants. A V36 bug was found
    and fixed along the way.
  - #702: LibreOffice under prlimit, zip-checked.
  - #703: one-pass embedding read.
  - #705/#707: certificate expiry; Open WebUI parity.
  - #706: NetworkPolicies on by default.
  - #708–#710: admin UI fixes.
  - #711 and #712: the small-fix batches. Admin-console is seeded, and managed clients are unique per (kind, name), as
    the user decided.
  - #720: the key-residency message.
- **Decided by the user:**
  - #726: tighten-only `ume.general_knowledge`, built in #736.
  - U1 waits for T6.
  - 0.6.0 is cut after the queue.
  - The Eneo comparison is committed and the ten features written up as a proposal.
  - Managed clients are unique per (kind, name).
- **Designs** brought up to date for C7, C11, C12, #696, #700, #706, #711, #712 and #720. Repo-and-deployment now has a
  Portainer section (§7.1).
- **Release 0.6.0:**
  - CHANGELOG, upgrade guide and archive (#745).
  - The browser tests fixed over nine CI rounds (#748).
  - The tag run failed at the Portainer boot: the read-only setup job's inline config (#749). The next dry run is the
    `--wait` problem above.
- **Lessons** (handbook, *Release dry runs*):
  - A dry run with e2e skips `build` when e2e fails.
  - The stale ghcr.io login denied the e2e stack's pull.
  - Run e2e between releases.
  - No inline config in a read-only service.
- **The parallel implementor agents** worked well: about 12, one item each, in their own worktrees. Read each diff,
  rebase, re-test the overlaps, and open the PR yourself.

