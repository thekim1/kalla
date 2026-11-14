# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.5.1`** (2026-11-13). Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every
commit).

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

## Start here: M-17, the deploy-configuration steps (session 35, 2026-11-14)

The plan the last handover asked for is **written and decided**: [`plans/proposals/deploy-configuration.md`](plans/proposals/deploy-configuration.md)
(D1–D10 as recommended; Decisions "Deploy configuration", "Residency per level", "Role groups as a setting"; the
gateway's part is thekim1/llmgateway #87–#90). The work is `plans/work/main.md` **M-17**, steps C0–C13, one PR each.

- **C0 is built, waiting for a merge:** #674 (the release boots the Portainer stack with `deploy/tests/portainer-boot.sh`;
  `build-stack.sh --check` in deploy-checks; `build-stack.sh --images` and the pin pull request after a release). Its
  done-when is met: release dry run 43536989470 green, a broken stack fails locally. Not yet exercised: the pin pull
  request (first run at the next tag release; it needs the repository setting *Allow GitHub Actions to create and
  approve pull requests*).
- **#678 goes first:** `opentelemetry-api` 1.45.2 in the parser's lock. Without it `parser.yml`'s `pip-audit` is red
  (CVE-2026-126360) and no release or dry run reaches the build. #674's branch carries the same commit by a merge.
- **C1 is built on the branch `a/m17-c1-public-url-default`** (on top of #674, no pull request yet): rag-api's public
  URL defaults to `https://<UME_RAG_API_HOST>`, the checklist no longer demands the platform-admin group, the boot test
  checks both. Open its pull request once #674 is merged (rebase on `main` first). Compose is unchanged on purpose:
  `init-deployment.sh` always writes the URL.
- **Then C2 and C3** (no decision, no gateway change), C4–C6, C8–C10, C13; C7, C11 and C12 wait on the gateway's
  releases. A step that changes rag-bootstrap's checklist updates the expected items in `portainer-boot.sh`.
- **The session that wrote this could not merge pull requests or push to `main`** (its permission settings): the
  merges above, M-17's tick for C0 and the roadmap line are left for the user or the next session.

## State

- **`v0.5.1` released** (2026-11-13; release run 43436813015): the Portainer stack with every image pinned and Open
  WebUI as the profile `openwebui` (#657), `docs/portainer.md` (#658), the variables file, `rag-bootstrap`'s checklist
  and secret files that reload (#659), #653's read-back texts. The repository's stack file is pinned to 0.5.1 (#661)
  and identical to the release's `compose.portainer.yaml` asset: give either to whoever deploys.
- **Not boot-tested:** the Portainer stack's Open WebUI profile (#657) and the whole stack were merged without a boot
  (user's call); the first VM deploy is the test (handbook *Lessons*).
- **Now (roadmap):** the **test chatbot** on DoIT's uploaded documents for a small test group, on the gateway and Ume.Rag
  deployed by hand by the user with Portainer (T0–T6, M-15); then the umea.se pilot. Open question for the user and the
  data protection officer: does the test group's use need the DPIA (G1) first?
- **M-16** (connect an app in one go, the platform menu, the Settings page) is another session's, on stream b's
  machine: steps 1–8 are merged (2026-11-14); #667 says what is left of it.
- A-41 is done (#665).
- **Stream b** (remote, GitHub only) works fast, answers findings with PRs, announces on #580. B-42–B-46 are merged
  and unticked until their live checks.
- **Local user secrets** still block the AppHost (handbook *Environment notes*).

## Next

1. Merge #678, then #674; tick C0 in M-17 and note it in the roadmap.
2. C1's pull request from `a/m17-c1-public-url-default`, then C2 and C3.
3. GitHub (new stream-b PRs and issues; #580, #513, #550 stay open).
4. When the user's VM deploy reports problems: fix the stack (Open WebUI's wrapper, CA bundle and data volume first).

## Session 35 (2026-11-14)

- **M-17 C0** built and proven by a release dry run (#674); **C1** built on its branch.
- **Parser audit fixed** (#678): the lock's `exclude-newer` moved to 2026-11-07, `opentelemetry-api` 1.45.2.
- This machine's Docker has no free address pool for the stack's networks: `portainer-boot.sh --compose-file` with fixed
  subnets (10.210.N.0/24) works. Port 5000 is taken here (a local registry must use another port). The `gh` token has
  no `read:packages`, so Ume.Rag's released images can't be pulled here: build them into a local registry instead.

## Session 33 (2026-11-13)

- **CI:** `backend` ~6.2 → ~4.5 min (#648 AdminApi tests in four database collections; #647 skips docs- and deploy-only
  PRs); #645 fixed (#650).
- **Releases `v0.5.0` and `v0.5.1`**; V39 confirmed (#651).
- **Plan files restructured** (M-14, #655, #656): the roadmap is the plan ahead, queues hold open items, designs hold
  the specification, history in `plans/archive/design-history/`, proposals in `plans/proposals/`.
- **Test chatbot before the umea.se pilot** decided (Decisions, roadmap *Now*).
- **Deploy with Portainer:** #657 (stack: pinned images, Open WebUI), #658 (`docs/portainer.md`), #659 (variables file,
  checklist, secrets reload). Stream b fixed #642 in #653 (reviewed); A-41 queued for #654.
