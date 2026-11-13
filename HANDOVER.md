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

## Start here: a plan to move env files into settings and automatic setup (user, 2026-11-13)

**The goal of the next session is a plan, not code:** push as much of the deployment's environment variables as
possible into run-time settings (`Settings.<Section>.<Key>`, the admin UI) or automatic setup, so a deploy needs only
what a process can't start without. Write it as a plan document (in `plans/proposals/` or as queue items, the user's
call), with each variable's destination, the decisions it needs, and an order. Ask the user before building anything.

**Starting material, measured in session 33:**
- **The surface today:** the Ume.Rag part of the Portainer stack reads **48** `UME_RAG_*` variables
  (`deploy/portainer/compose.rag.template.yaml`, listed with comments in `deploy/portainer/stack.env.example`); Compose
  reads 41 (`deploy/compose.prod.yaml`, `compose.ingestion.yaml`), written by `deploy/init-deployment.sh` (723 lines).
  Secret files come from `rag-bootstrap` (Portainer, `deploy/portainer/bootstrap.sh`) or `init-deployment.sh`.
- **Binding rules to plan within:** Decisions "Settings rule" (deployment knobs `Section:Key`, run-time values in the
  database); Decisions "Gateway keys" (each setting has one owner: departments, teams, models and a key's settings are
  the gateway's; which key an object runs on, prompts, retrieval are Ume.Rag's); the services read secrets from files,
  never from environment variables.
- **A first sort, to check, not decided:**
  - *Stays deploy-time:* host names and public URLs, ports and binds, the OIDC authority, database and certificates,
    profiles, image pins, resource limits.
  - *Could become settings:* the embedding model (already "seeded once", then edited in the UI: the variable could
    go), the OIDC group-to-role mapping (`UME_RAG_OIDC_GROUPS_*`; the first platform admin is the chicken-and-egg),
    `UME_RAG_GATEWAY_MAX_RESIDENCY`, `UME_RAG_CRAWL_ALLOW`, the Open WebUI feedback import's URL and interval, the
    ClamAV mode, the telemetry sampler; `UME_RAG_GATEWAY_INTEGRATION_API_URL` might come from the gateway's context
    (`admin_url`): check in the gateway's code.
  - *Could be set up automatically:* the three gateway credential keys (a provisioning step against the gateway's
    admin API, which needs a gateway change: #3 of the deploy-ease list), Open WebUI's client and key (rag-admin creates
    them when the `openwebui` profile is on), the feedback service account's key (a one-shot job), the OIDC clients
    (Keycloak realm import, `deploy/keycloak/`), the embedding model picked from the gateway's `/v1/models`.
- **The deploy-ease list** (session 33, user chose 1 and 2, both built in #659): 3 automatic gateway integration
  set-up (gateway change), 4 an optional TLS proxy profile in the stack, 5 each release boot-tests and pins the
  Portainer stack. They belong in the same plan.

## State

- **`v0.5.1` released** (2026-11-13; release run 43436813015): the Portainer stack with every image pinned and Open
  WebUI as the profile `openwebui` (#657), `docs/portainer.md` (#658), the variables file, `rag-bootstrap`'s checklist
  and secret files that reload (#659), #653's read-back texts. **The stack template still pins 0.5.0's images:** pin it
  to 0.5.1's (from the release's `compose.portainer.yaml` asset) so the no-restart key changes apply; the docs already
  say "from 0.5.1".
- **Not boot-tested:** the Portainer stack's Open WebUI profile (#657) and the whole stack were merged without a boot
  (user's call); the first VM deploy is the test (handbook *Lessons*).
- **Now (roadmap):** the **test chatbot** on DoIT's uploaded documents for a small test group, on the gateway and Ume.Rag
  deployed by hand by the user with Portainer (T0–T6, M-15); then the umea.se pilot. Open question for the user and the
  data protection officer: does the test group's use need the DPIA (G1) first?
- Every code item is done except **A-41** (#654: the read-back on the platform's Gateway view, texts on a
  `main-session/` branch).
- **Stream b** (remote, GitHub only) works fast, answers findings with PRs, announces on #580. B-42–B-46 are merged
  and unticked until their live checks.
- **Local user secrets** still block the AppHost (handbook *Environment notes*).

## Next

1. **The plan above** (the session's goal).
2. Pin the Portainer stack to 0.5.1's images, if not done; GitHub (new stream-b PRs and issues; #580, #513, #550 stay
   open); A-41.
3. When the user's VM deploy reports problems: fix the stack (Open WebUI's wrapper, CA bundle and data volume first).

## Session 33 (2026-11-13)

- **CI:** `backend` ~6.2 → ~4.5 min (#648 AdminApi tests in four database collections; #647 skips docs- and deploy-only
  PRs); #645 fixed (#650).
- **Releases `v0.5.0` and `v0.5.1`**; V39 confirmed (#651).
- **Plan files restructured** (M-14, #655, #656): the roadmap is the plan ahead, queues hold open items, designs hold
  the specification, history in `plans/archive/design-history/`, proposals in `plans/proposals/`.
- **Test chatbot before the umea.se pilot** decided (Decisions, roadmap *Now*).
- **Deploy with Portainer:** #657 (stack: pinned images, Open WebUI), #658 (`docs/portainer.md`), #659 (variables file,
  checklist, secrets reload). Stream b fixed #642 in #653 (reviewed); A-41 queued for #654.
