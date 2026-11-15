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

## Start here (session 40): a design session on a setup wizard

**The user's idea (2026-11-15):** move most of what a deploy sets in environment variables (Portainer's stack
variables, `deploy/.env` written by `init-deployment.sh`, the Kubernetes values) into an **onboarding / setup wizard
in the UI**, so the whole platform is easier to set up. This session is design, not code: work it out with the user
and end with an agreed proposal.

**Build on M-17, don't start over.** The deploy-configuration work (2026-11-13, `plans/proposals/deploy-configuration.md`,
Decisions "Deploy configuration", M-17 C0–C12 merged) already went from 48 `UME_RAG_*` variables and three paste
round trips to **one pass**:
- what's left: host names, ports and binds, the OIDC authority, client ids and secrets, the crawler's private ranges,
  profiles, image pins, telemetry;
- the gateway keys come from the gateway's manifest, Open WebUI's client and the feedback key are made by the stack;
- the embedding profile is a first-run checklist item in the admin UI (C5; admin design, the overview's *Get started*).

Proposal §2.1 gives the reason each remaining variable stays. The wizard is about those.

**The decision this has to change, with the user:** Decisions "Deploy configuration" keeps exactly those values
deploy-time on purpose. A wrong OIDC value set in the UI locks every admin out, the OIDC handler caches its options,
and a compromised admin account must not be able to change the security boundaries (OIDC, `Crawl:AllowedPrivateRanges`,
D4). A wizard needs a sound answer to each, then a changed decision entry (and a dated line in the decisions log).

**Questions to settle** (present each as four alternatives with a recommendation, as the user prefers):
1. **Scope:** Ume.Rag only, or the whole platform with the gateway (the user's repository, `~/git/llmgateway`, has its
   own Portainer variables and admin)? Portainer only, or Compose (`init-deployment.sh`, 723 lines, 30 options) and
   Kubernetes too?
2. **Who may use the wizard before sign-in works:** for example a one-time setup code that `rag-bootstrap` writes to
   its log or a volume, a setup mode that locks itself when done, bound to localhost or the admin host only.
   How do other platforms do it? `platforms/` has Open WebUI, Dify, Onyx, RAGFlow and Eneo; also look at
   Grafana's, GitLab's and Nextcloud's first run.
3. **Which values move, and which stay:**
   - **Host names:** they're certificate SANs, written before anything starts (`rag-bootstrap`'s CA; the proxy uses
     the operator's certificate).
   - **OIDC:** the wizard would set up the sign-in it is itself behind; a test sign-in before saving?
   - **Open WebUI:** it reads its environment at start (`ENABLE_PERSISTENT_CONFIG=false`).
   - **The crawler's private ranges:** a security boundary that mirrors the host firewall in Compose.
4. **How a changed value takes effect without a Docker socket** (no service gets one): reloadable options
   (`IOptionsMonitor`), or a service that exits and comes back through `restart: unless-stopped`. Which values are read
   only at start is listed in the proposal §1, *How the code reads them*.
5. **Where values live:** settings in the database (`Settings.<Section>.<Key>`, Decisions "Settings rule"), and
   secrets as files in a volume, never environment variables (repo-and-deployment §6.3).
6. **What the deployer still types into Portainer,** ideally almost nothing: measure it against today's
   `stack.env.example` (13 lines, 11 marked CHANGE, some of them the gateway's).

**Read first:**
- `plans/proposals/deploy-configuration.md`: §1 what a deploy asks for and how the code reads it, §2.1, §6.
- Decisions "Deploy configuration", "Settings rule", "Gateway keys", "Shared sign-in with the gateway".
- `docs/portainer.md` (today's steps), `deploy/portainer/stack.env.example`, `deploy/portainer/bootstrap.sh`
  (`rag-bootstrap` and its checklist), repo-and-deployment §6.3 and §7.1.
- The admin design's first-run pieces: the overview's *Get started* and the embedding-profile step
  (`plans/admin/design.md` §5.4 and §5.5.1 *Home*).

**Output:** `plans/proposals/setup-wizard.md` (status *proposed*): what changes for the person deploying, before and
after, as in proposal §6; the decisions for the user; the gateway changes, if any, as issues for the user's repository;
the steps as an M-item in `plans/work/main.md`. No code until the user agrees.

**Also open (not this session's topic):**
- **The user's upgrade to 0.6.0** (`docs/portainer.md`, *Upgrade from 0.5.x to 0.6.0*). Before it, rag-api's and
  rag-admin's credential keys get `OnPrem` and `Eu`, and each published assistant needs a gateway key that reaches both
  its chat model and its embedding model. Pulls that say "denied": `docker logout ghcr.io`.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
  F3, F4, F8 and F10.
- **0.6.1:** C13 (the M-16 session, #667); the release files are free again.
- **The next release run checks three things for the first time:** `:latest` moves in `publish` (#756); verify and
  parser run in full on a tag, a moved one too (#755); the run takes about 17 min (0.6.0: 27).
- **For the user:** *Allow GitHub Actions to create and approve pull requests* is still off (Settings → Actions →
  General), so `release.yml` couldn't open the pin PR (#754 was opened by hand).
- **Checks before go-live:** the 16 *Before go-live* rows of `plans/to-verify.md` need no input (the main session's
  suggestion for work alongside).

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
