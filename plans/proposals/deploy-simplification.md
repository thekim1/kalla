# Deploy simplification: one bootstrap, fewer special cases

**Status:** **proposal** (main session, session 45, 2026-11-16), from an analysis of a first install and of the install
and upgrade guides (#794, #795, #796). The user asked for it to be written down and for the work to start in the next
session (2026-11-16), and added S9 (Open WebUI easy to skip); the recommended start is S1, S3 with S9, and S4a. S2 and S5 change the security design or the gateway and
wait for the user's decision. Nothing here binds until it is adopted in `decisions.md`; the work is `work/main.md`
**M-22**.

**Goal:** the same system deploys the same way in every form, and an operator does only what a person must do. Today
the complexity is mostly duplication: the deployment is implemented three times, and the two products each bring
their own copy of everything.

## Where the complexity is

| # | What | Why it costs | Simplification |
|---|---|---|---|
| S1 | **Three deployment forms, each with its own bootstrap.** Compose: `deploy/init-deployment.sh` (850 lines, about 40 options). Portainer: `deploy/portainer/bootstrap.sh` inside a generated stack file. Kubernetes: `create-secrets.sh` and `deploy.sh` | Certificates, secrets, retired variables and Open WebUI's settings exist in each, with tests whose only job is to keep them equal (`deploy/tests/openwebui-parity.sh`). Portainer needs one typed value, Compose a long procedure, for the same system | Compose uses the bootstrap container Portainer has; `init-deployment.sh` shrinks to writing a small `deploy/.env` |
| S2 | **Compose carries host machinery Portainer does without:** fixed subnets in `172.30.0.0/16`, the iptables firewall on a timer, the systemd slice, the cgroup driver, `daemon.json`, Compose 2.33.1 for `gw_priority` | Everything must be in place before the first `up`. The firewall is the source of the special cases: the sign-in provider it must be told about, the `--allow` drop-in for the cost read-back, the one for Open WebUI's provider. The two forms have different security postures | The firewall and the slice become an optional hardening step after a working install |
| S3 | **Open WebUI is a second Compose project** | A script re-run, an `up` of its own and a firewall restart; in Portainer it is the profile `openwebui` | A profile in Compose too |
| S4 | **Upgrades are fragile because every release changed the contract:** seven releases in two weeks, 20 retired variables, a 550-line upgrade guide, a stack file per release applied in order, a manual stop for *Rolling update: no* | An operator must read three sections to move one VM | (a) delete the upgrade paths nobody runs; (b) the migration step stops what it must itself; (c) test an upgrade that skips a release, so it is one paste |
| S5 | **Two products, two of everything:** two setup wizards with two codes, two sign-in clients registered by hand, two internal CAs, two databases, two bundles | The second wizard repeats the first's work | After the gateway's wizard, the gateway hands Ume.Rag a ready link (no second code, no provider address); with the gateway's own Keycloak, Ume.Rag's clients are applied by themselves |
| S6 | **Too many things to back up separately, and differently per form:** the pepper, the Data Protection certificate, the blob key, the pgBackRest passphrase, the CA key; Compose has pgBackRest, escrow and timers, Portainer manual dumps | A restore without one of them is unusable, and the list is spread over three guides | One command that writes one escrow set with all of them, the same in both forms |
| S7 | **Gateway keys still need hand work after the manifest:** the integration is moved to a team, and assigned keys are created per department before an assistant can be published | The first assistant waits on a step no page asks for | The manifest names the owner at install and creates a first assigned key |
| S8 | **Operator documents carry project history:** about 5,000 lines on twelve pages, with phase names, item IDs and issue numbers | It is why so much had gone stale (#795 corrected about 80 statements) | Operator pages without project vocabulary; the script's `--help` is the only option list |
| S9 | **Open WebUI is assumed in places although not every installation needs it** (user, 2026-11-16) | `deploy/portainer/stack.env.example` presets `COMPOSE_PROFILES=clamav,openwebui`; `UME_DOMAIN` names a `chat.` host and the stack's proxy serves five host names; the last step of *Get started* is *Make available in Open WebUI*; the first-install guides walk through it. The setup wizard already hides its Open WebUI step when Open WebUI is off | Off unless asked for, in every form, and nothing mentions it when it is off |

## Smaller items from the first analysis (the same goal, not started)

- **A readiness list on Ume.Rag's Home** for platform admins: the gateway reachable, each service's key accepted, the
  keys' residencies, an embedding model and a chat model, an assigned key in a department, trusted proxies, the last
  backup, Open WebUI's sign-in; each line links to the page or guide that fixes it. Today Home asks only for the
  embedding profile. A design change to the admin UI (admin §5.5).
- **Preflight and one-command start:** `init-deployment.sh --check` (cgroup driver, address pools, ports, the Compose
  version, a test embedding with each key), and a finishing step that offers the host units, `up`, `stanza-create` and
  the first backup. Fits with M-17's C13. Mostly absorbed by S1 if Compose takes the bootstrap container.
- **A first-run list in the gateway's admin** after the first sign-in: add a provider, add a model, create a team,
  connect Ume.Rag from the preset. In the gateway's repository; merges there are the user's.

## Recommended start: S1, S3 with S9, S4a

They remove the most code and documentation without changing what the system does.

### S1 One bootstrap

- **Today.** `rag-bootstrap` (Portainer) generates the CAs, certificates, secrets and derived files into the volumes
  `rag-certs` and `rag-secrets` from environment variables, is idempotent, re-issues certificates near expiry and
  prints a checklist of what is missing. `init-deployment.sh` does the same into `deploy/certs` and `deploy/secrets` on
  the host from options, and also writes `deploy/.env`, the proxy configuration, the ClamAV confs and the pgBackRest
  repository settings.
- **To decide first** (each as four alternatives with a recommendation, at the start of the work):
  1. **Where the files live in Compose:** Docker volumes as in Portainer (then escrow, `--ca-file`, PKI certificates
     and `create-secrets.sh` read from a volume), or bind-mounted host directories filled by the same container.
  2. **What stays in the script:** only `deploy/.env` (host names, the gateway's address, the backup target, the
     profiles), or nothing (a `stack.env` edited by hand, as in Portainer).
  3. **Whether Compose and Portainer become one stack file** with profiles for what differs (pgBackRest, the host
     firewall's subnets), or stay two files with one bootstrap.
  4. **Kubernetes:** `create-secrets.sh` reads `init-deployment.sh --no-chown`'s output today; it either runs the
     bootstrap container too or keeps a reduced script.
- **Constraints.** No Compose deployment exists outside tests (the user's VM is the Portainer stack), so Compose may
  change in one step (AGENTS.md, *No backward compatibility before production*). **Upgrades may break too** (user,
  2026-11-16: "assume no one is really using this yet", so a low-hanging simplification that breaks an upgrade is
  fine): a change need not keep an existing stack's volumes, variables or stack file working, as long as the release
  says so and says how to install again. Say it to the user when a change means the VM is reinstalled rather than
  upgraded. Decisions "Deployment values
  across releases" holds for every variable. M-17's C13 (the script loses the options whose values moved) is the first
  step of this and is still open.
- **Read first:** `deploy/portainer/bootstrap.sh`, `deploy/portainer/compose.rag.template.yaml`,
  `deploy/portainer/build-stack.sh`, `deploy/init-deployment.sh`, `deploy/kubernetes/create-secrets.sh`,
  `deploy/tests/portainer-*.sh` and `pilot-stack.sh`, repo-and-deployment §7.1.

### S3 Open WebUI as a profile in Compose

`deploy/compose.openwebui.yaml` moves into the main project as the profile `openwebui`, as in the Portainer stack
(`rag-openwebui`, `rag-openwebui-setup`); `--openwebui-host` adds the profile; the firewall rules for its network come
with the same run. Its sizing note stays (the 24 GB host). Falls out of S1 if the stack files merge.

### S9 Open WebUI easy to skip (user, 2026-11-16)

Not every installation needs a chat UI: some only serve the API to their own applications. To do, after checking each
point in the code (the list above is from reading, not from a run without Open WebUI):

- **Defaults:** `stack.env.example` without `openwebui` in `COMPOSE_PROFILES`, with one commented line to turn it on;
  Compose as S3. An existing stack keeps its profile, so nothing changes for the user's VM.
- **Nothing left over when it is off:** no `chat.` host name asked for or served by the stack's proxy, no Open WebUI
  client created by `rag-migrate`, no Open WebUI secrets generated, no note about it in the bootstrap's checklist.
- **The admin UI:** the last step of *Get started* names what the installation has (Open WebUI, or an app and its key
  under *Apps and keys*); the Open WebUI settings and the feedback page say that Open WebUI is not part of this
  installation instead of showing empty fields. Texts are in `labels.ts` and `messages.ts` (stream b's paths).
- **The guides:** the first-install paths (`compose-quick-start.md`, `portainer.md` *First deploy*) finish without
  Open WebUI, with one section *Adding Open WebUI* that is the same steps for a new and a running installation.
- **A test without it:** the Portainer boot test runs with `clamav,openwebui,proxy` only; one boot without the
  `openwebui` profile shows the stack is whole without it.

### S4a Delete the upgrade paths nobody runs

The only deployment is the user's VM on 0.5.0. `docs/portainer.md`'s *Upgrade from 0.2.0–0.4.0* and the upgrade guide's
sections for 0.2.0 to 0.4.0 can go (the changelog keeps the history). Since upgrades may break (below), every
per-version section but the newest can go; tell the user if that leaves the VM's 0.5.x path without a guide.

## Allowed now that upgrades may break (user, 2026-11-16)

Low-hanging fruit that an upgrade path was the only reason to keep. Check each in the code before removing it:

- the retired-variable list and its notice (`bootstrap.sh`, `UME_RAG_RETIRED_SET`, Home's deployment notice): a clean
  list at the next release instead of 20 names carried along;
- the clean-up branches in `init-deployment.sh` and `rag-bootstrap` for files of earlier releases (per-level gateway
  keys, the Data API key, refused old options);
- every per-version upgrade section but the newest, in `docs/upgrade-guide.md` and `docs/portainer.md` (S4a without
  waiting for the VM);
- renaming or merging variables and volumes where S1 is simpler with one layout for both forms;
- the hand-issued key path (`UME_RAG_*_GATEWAY_KEY`) beside the manifest, if one way is enough.

## Order after that

S4b and S4c (upgrades that need no manual stop and may skip a release), then S6 (one escrow set), then S8 as each page
is touched. S2, S5 and S7 when the user has decided them.
