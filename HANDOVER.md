# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.9.2`** (2026-11-21, tag at `c7d5de47`, on Fors `v0.12.2`). **`main` has unreleased code since
2026-11-23:** P2 wave 1's first four items, a source's schedule, the fix for the 502 after sign-in, the organisation's
logo and seven code-quality refactors (*State*). The user's VM runs 0.5.0. Three machines have worked on it:
`~/research/rag` (the user's, sessions 49 to 55), `~/git/ume-rag-platform` (the user's WSL machine, session 56) and
`thekim1`'s (stream b, stream a, the releases, and a coordinating session that writes proposals). *Start here* says
what does not travel between machines. Branch `main`, remote `origin` = https://github.com/thekim1/kalla.

## What this is

**Källa** (`Kalla` in identifiers; Decisions "Product names") is a RAG / crawling / chatbot platform for Umeå kommun:
small .NET 10 services on top of **Fors**, the team's LLM gateway (https://github.com/thekim1/fors, the local clone
still `~/git/llmgateway`), as an add-on to it (Decisions "Shared sign-in with the gateway"). The user owns the gateway
and wants to do better than **Eneo**, a slow "kitchen sink" monolith. Design and code live in this repo.

## Where to read

1. **[`plans/roadmap.md`](plans/roadmap.md): the plan ahead.** Done by release, the critical path to go-live with each
   step's input and owner, work that needs no input, and the open calls. HANDOVER doesn't repeat it.
2. [`plans/decisions.md`](plans/decisions.md): the decisions in force; every entry binds. Record a decision by changing
   or adding an entry, then a dated line under *Later decisions* in `plans/archive/decisions-log.md`.
3. **[`plans/work/main-handbook.md`](plans/work/main-handbook.md): how we work (the user's preferences, binding), the
   local stack, lessons, environment notes.** Read it at the start of a session; add a lesson there when you learn one.
4. `plans/work/`: the queues `stream-a.md`, `stream-b.md`, `main.md` (open items only; finished ones are in
   `plans/archive/work/`), results in `results-a.md`, `results-b.md` and `results-main.md`.
5. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and doesn't bind.

## Start here (session 57)

**What does not travel between machines:**
- **Claude Code's own memory stays on the machine it was written on.** Everything that binds is in
  [`plans/work/main-handbook.md`](plans/work/main-handbook.md) (*How we work*); read it before anything else.
- **Accounts:** the session works as `ume-nichlas`, and approvals are the user's (`Crotalus`). Both accounts are in
  `gh` on `~/research/rag` and on `~/git/ume-rag-platform`; run `gh auth status` first.
- **Pushing:** on both of the user's machines `gh` is the credential helper (below); it works on
  `~/git/ume-rag-platform` too (GitHub answers with a "repository moved" note, harmless).
- **Fors:** `~/git/llmgateway` on `~/git/ume-rag-platform`'s machine is at `6c3c412` (Fors `main`, two commits past
  `v0.12.2`), the commit Källa's contract copy is pinned to. Elsewhere clone https://github.com/thekim1/fors beside
  this repository.
- **Docker** on `~/git/ume-rag-platform`'s machine is 29.9.3 (the user upgraded it in session 56) and must be started
  by hand before Testcontainers tests. *State*'s machine block describes `~/research/rag` only.

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch. `thekim1` merges green pull requests with `--admin` (allowed
  by the user on 2026-11-19), its own included.
- **Release tags are protected** (`refs/tags/v*`): a tag is `thekim1`'s or the user's to push, on the release
  preparation's merge commit, with the user's go-ahead.
- **The session's GitHub account is `ume-nichlas`.** `gh` here also holds `Crotalus` (the user's own). **Run
  `gh auth status` before every push or post.**
- **Approvals on this machine.** Approving as `Crotalus` goes through on the user's direct word in the conversation
  for named pull requests ("approve #936, #937, #938"): read each pull request's state and checks, switch the account
  in a command of its own, approve, switch back at once, then merge the green ones. Without that word: review as a
  comment, say that it is not the approval, and give the user the line to run (handbook, *How we work*). **Stacked or
  overlapping pull requests:** after the first is squash-merged the next conflicts; a rebase and push dismisses its
  approval, so it needs the word again (session 53: the same word for the same named pull requests was accepted).
  **The word covers the pull requests open when the user wrote it** (session 55): "review / approve etc" and "approve
  the green PRs" went through for those; a batch that `thekim1` opened afterwards was stopped by the permission check.
  After a refusal: switch back to `ume-nichlas` at once, leave a "ready" comment on each, give the user the line.
- **Push with `gh` as the credential helper:** a plain `git push` on `~/research/rag` asks the Windows credential
  manager and hangs.
  Use `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push …` (session 54).
- **Look at the open pull requests before each step** (`gh pr list`): the other machine works at the same hour. Say on
  its pull request what you are about to write; review its pull requests as comments (claims checked against the code).
- **A decision for the user is asked as four alternatives with one recommendation** (the question tool).

**Open when this was written** (2026-11-23, about 13:15 UTC): Dependabot's #958 and the main session's #989 (this
file), #990 (the support case, B-83) and #991 (B-58, B-59 and B-63's design deviations). `thekim1` merged #977 (B-58),
#963 (B-63), #978 (B-59) and #984 (B-58's Settings switch) at about 13:00, each after the main session's review said it
was ready. In order:

1. **#990 and #991**, the main session's own: green or nearly so; they need the user's word.
2. **B-58, B-59 and B-63 are merged; their design deviations are #991.** It also writes down thekim1's hand-off on
   #963: the personal-data preview's output is read at `Edit` while starting it needs `Manage`. That is sound (the
   spans hold positions, never values, `PreviewPiiSpan`; an editor already reads the text through the chunks); only
   reading the output is not audited. If that read should be audited or raised to `Manage`, that is a stream-b change.
3. **#981's follow-ups** (on #981, for #980): B-57's `TestQuestionDrawer.vue` has a hand-rolled debounce with no unmount
   clear, `AssistantTestsTab` a fixed poll; `RunView` and `PublicationView` don't reload on a `props.id` change.
4. **#978's smaller points not taken** (its review): weak tests for the key item on a knowledge base, `owner_inactive`
   and `not_usable`; `ATTENTION.none` unused; the `personal_data_public` link now opens Documents with `?pii=flagged`.
5. **#958, the parser's bumps:** `LICENSES.md` has to be regenerated on the branch; stream a's. Best taken with opencv
   5.1 (item 9).
6. **#983, the security read of B-54 to B-57 (release blocker, stream b):** a draft's test panel and B-57's test runs
   search knowledge bases not shared with the assistant's department (`KnowledgeService.AssistantCollections` never
   checks sharing; the panel's snapshot confines only unpublished collections), and copying an assistant across
   departments carries the links over, so it is the normal path; copied test questions keep the source department's
   documents; no rate policy on `POST /test-runs`. Item 2 (exception text in the judge note) is fixed by #977. Fix
   before the next release. When it is settled, the main session writes the copy's rule for links and expected documents into admin
   §4.5 (copy row) and §4.9/§7.4 (the panel and the runs search only what V11 allows); #985 left them out on purpose.
7. **Read of the never-read documents (handover 56's item 11):** read 1 is done (#988: `to-verify.md`,
   `postponed-decisions.md`, the gold-set README; issues #986, #987). Read 2 is #990 (the support-assistant case
   brought up to date with the code and decisions; the user's four answers: the case's framing stays, condense keeps
   3 s, the rerank model is a deployment's choice, DoIT's key uses PII policy `Allow`; SA-11 queued as B-83). Read 3 is
   `docs/` (stream a's: findings become `stream-a` issues).
8. **The code-quality plan** ([`plans/proposals/code-quality-top7.md`](plans/proposals/code-quality-top7.md), tracked
   in #980): done are #1, #2, #4, #5 (#981), #6 and #7; #5b and #3 are `thekim1`'s next, now that #963 is in; stream
   a's parts are #974.
9. **opencv 5.1 from 2026-11-25** (*Watch*): try `opencv-python-headless` 5.1.0.95 in the parser once its uv cutoff
   lets it in; it may retire the eleven FFmpeg exceptions before 2026-12-17.
10. **#925's steps 2 to 4** (personal-data verdicts, *reject*, the false-positive summary): the Decisions entry and the
    design changes are `thekim1`'s session's (said on #925). Step 1 is B-63 (#963, merged).
11. **The VM's upgrade is `thekim1`'s** (#920): 0.5.0 straight to 0.9.2. No report yet.
12. **Repository variables:** the workflows read `vars.KALLA_TEST_HOST_RUNNER`, `vars.KALLA_TEST_HOST_DEPLOY_ENV`,
    `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`, and none exist; `thekim1` was asked on #905 (2026-11-21), no
    answer yet.
13. **Before Källa's next release, Fors needs one.** `contracts/gateway/` is pinned to Fors `main` at `6c3c412`
    (thekim1/fors#126, "no release yet"), and the logo of #953 shows only on a Fors that has it. Then the contract
    README's *Taken from* names the release and the Portainer stack pins it. Fors merges and releases are the user's.
    #983 also blocks the release.

### Waiting on the user

- **The umea.se inventory (A-L1):** the first input of the live block (`plans/roadmap.md`); every live item waits on an
  input, A-L1 first.
- **The NVDA pass (to-verify row 96):** the user's team runs it on Windows with `plans/work/nvda-pass.md`. Its
  checklist names today's tabs, so hold it until B-78 (UX3) and B-79 (UX2) have landed; the main session then rewrites
  the checklist once; the result goes in `results-main.md`.
- **The gold set's scope:** the draft covers the 1,166-page "Kommun och politik" slice; whether DoIT extends it to the
  full umea.se crawl or accepts the slice is open (`plans/gold-set/README.md`).
- **The support-chatbot proposal:** F3, F4, F8 built; F10, F7, F6 queued (B-61, B-62, B-66); F1, F2, F5, F9 are the
  user's pick (F2 and F1 best after DoIT's real tickets).
- **P2's wave 3** (the roadmap's *P2 Broaden*): the user picks when wave 2 is nearly done. Stream a has only the live
  block and #974, so a theme with stream-a work would use both streams: Tools and MCP (the M1 and V1 re-checks of
  `postponed-decisions.md` first) or ingestion breadth (#511, the worker split, first).
- **To-verify row 79:** whether `NerMinScore` moves for the int8 NER model (left at 0.80: the sample is small).
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).

### Watch

- **The runners give way under load.** Three backend test runs at once started 44 Testcontainers and took
  `thekim1`'s WSL VM down (2026-11-23); that session now runs its items one at a time. The signs: a `backend` job red
  with no failed step and no log (#970), or many unrelated test classes failing in the same second (#969). Re-run the
  failed job (`gh run rerun <run> --failed`) before calling a failure real. With many pull requests' checks on the two
  runners, tests the change didn't touch fail; run alone they pass.
- **A check with no conclusion is queued, not green.** Session 55 called #976 green before its checks had started:
  read `conclusion // status`, and treat empty as waiting.
- **The CI runners' disk filled up once** (2026-11-21, the 0.9.2 dry run's parser image job; `thekim1` re-ran it).
  If a long job is cancelled with no step result again, look at the runner machine's disk too.
- **The dated exceptions expire:** the scan's FFmpeg CVEs in the parser's opencv wheel (eleven, until 2026-12-17,
  `scripts/audit/image-scan-exceptions.txt`, #818) and picomatch in the admin UI's build (until 2026-12-18,
  `scripts/audit/npm-production-exceptions.txt`, #853). Before then: a newer wheel or picomatch fix, a new date with
  a reason, or drop the dependency. **opencv-python-headless 5.1.0.95** came out 2026-11-17 (the pin is 4.14.0.94);
  the parser's uv cutoff lets it in from about 2026-11-25: try it (Docling needs only `cv2.resize`, `cv2.dilate` and
  border constants) and see whether its wheel bundles a fixed FFmpeg. picomatch had no fix on 2026-11-20.
- **The Postgres image's `zlib1g` line** (`images/postgres/Dockerfile`, #907) goes when `pgvector/pgvector` has a
  build newer than 2026-11-12.
- **Flaky tests:** #781 (the "search unavailable" answer in `ChatApiTests`) stays open until 2026-11-30; if it recurs,
  the job's log has *The query embedding of profile … is unavailable (…)*, which names the cause. #921's render test
  got a limit far above its own work (#970), and the conversation writer's and the reranker's elapsed-time assertions
  wider margins (#969); #921 is `thekim1`'s to close.
- **`gh pr edit` fails** on this repository (*Projects (classic) is being deprecated*) and changes nothing; edit a pull
  request with `gh api -X PATCH repos/thekim1/kalla/pulls/<n> -f title=… -F body=@file`.
- **Tests that share the job queue:** the nightly purge re-queues `delete-source` for every deleted source with live
  content (#884), so a test that queues a command for a deleted source can lose its lease (#897).
- **The demo seed's slug in a test:** `openwebui.spec.ts` leaves out the assistant named `demo-assistent` (#882).

## What session 56 did (2026-11-23)

On `~/git/ume-rag-platform` (the user's WSL machine), as the main session. Session 55's work is in this file's
history. Reviews and reads ran as subagents, one at a time; their claims were checked in the code before posting.

- **The UX pass decided** (the user, six questions): UX1 A (B proposed to Fors), UX2 to UX6 C. **#982:** the eleven
  layout rules and the choices in Decisions "Admin API and UI", admin §5.12 (new) and §5.2 to §5.10 rewritten, the
  proposal marked decided, the build steps queued as **B-74 to B-81** in stream b's new section *Admin UI UX pass*.
- **Reviews as comments:** #978 (B-59), #981 (CQ-5), #984 (stream a's switch), and the re-reviews of #977, #978, #984
  and #963 once `thekim1` had fixed them (each claim checked, the touched tests run locally); `thekim1` merged all four.
  **Security read of B-54 to B-57** filed as #983 (`release-blocker`).
- **#985:** B-54 to B-57's design deviations recorded (admin §4.5, §5.5.4, §7.4; chat §8.1; data-model §2.3), and
  **B-82** queued (the general Publish dialog with the diff, the warnings and the cost; the design had it, nothing
  built it).
- **#988:** the first read of the never-read documents. `to-verify.md` (15 done rows archived, rows 182 to 185 for the
  go-live gaps, every live-block row named by a queue item), `postponed-decisions.md` (V1, Z1, S1, P2 brought up to
  date), the gold-set README (how the draft becomes `human` questions). Decisions "CI/CD" now says actions are
  SHA-pinned (a stale fact, not a new decision). Issues #986 (stream b) and #987 (stream a).
- **#990:** the support-assistant case read through and brought up to date (four answers from the user, and SA-11
  queued as B-83: a known file's upload replaces it by default, admin §5.5.7 point 6).
- **#991:** B-58, B-59 and B-63's design deviations (processing §14.8, admin §4.5, §5.2, §5.5.1, §5.5.2, §5.5.3,
  §10.3, chat §8.1, data-model §2.2, §2.3).
- **Approved as `Crotalus` on the user's word and merged:** #981, #982, then #985, #988.

## Known gaps, documented, not fixed

- **The Kubernetes upgrade to 0.9.0** (the upgrade guide's six steps) is read through, not run: no cluster, and
  Kubernetes is rendered only. The Compose upgrade script `upgrade-to-0.9.sh` has no test of its own either: its
  variable step was run by hand (#905), its volume copy only read.
- **Not tested anywhere:** Portainer itself (an upgraded stack boots in CI since #817, with Compose; from 0.6.0 since
  0.8.0); both wizards completed against a real provider in one deployed stack (the boot test has no provider and
  stops at each wizard's state); Entra ID and AD FS; a real untrusted TLS certificate; Kubernetes beyond
  rendering.
- **The manifest default needs Compose 2.24.7 or later:** an older one cuts a default that contains `}` at the first
  one, without an error, also when the variable is set. Portainer pins a later one from 2.21 (read, not run).
- **A stack without `FORS_INTEGRATION_MANIFEST`** (0.5.x style, keys issued by hand) gets the default from 0.7.0: three
  unused credential keys unless the variable is set empty first (`docs/portainer.md`, *Upgrade from 0.6.0*).
- **The boot test shows no Open WebUI with OIDC** (no provider anywhere, so it starts without sign-in); `e2e.yml`
  shows it (accepted by the user).
- **A hardened Compose host's firewall** lets kalla-admin reach only the provider of `--oidc-authority`; without it
  the wizard's test can't reach a provider there. `init-deployment.sh --hardened` asks for it in an interactive run,
  and its closing lines say when it is missing. A host without the hardening has no such limit.
- **The Compose quick start (`docs/compose-quick-start.md`) was run once, by hand, not as written** (*What session
  49 did*): with locally built images, without root, and without a provider, so its steps 5.2 to 5.4, the host timers
  and the hardening are still only read. No test runs the two products' Compose projects together; the release
  smoke test boots `compose.rag.yaml` with the backup overlay against the fake gateway (since #872 also in the
  host-only form).
- **The gateway's Data API does not follow its wizard:** a gateway whose sign-in is set there sets
  `FORS_DATA_AUTHORITY` for access tokens. On Kubernetes a gateway that declares `Oidc__Authority` must set
  `Gateway__OidcAuthority` too (documented).
- `GET /internal/openwebui/sign-in` is closed at the Portainer proxy; in Compose only the managed client's key opens
  it.
- **Open WebUI v0.12.5, upstream** (to-verify 47, 165): text-fragment links lose å, ä and ö, so Swedish highlights
  don't match; a PDF snippet link gets two `#`; a workspace override of an assistant replaces its description.
- **`tests/e2e/specs/openwebui.spec.ts` is a by-hand spec** (project `openwebui`; the stack needs
  `FakeGateway__Latency__TokenIntervalMs=80` and `FakeGateway__Latency__AnswerWords=150` for its Stop check): not in
  `e2e.yml`.
- **The crawl setting `IncrementalSchedule`** is checked only for five tokens (nothing reads it yet); sources'
  schedules and `Settings.DefaultSchedules` are parsed properly since #791.
- **CI:** what `AdminApi.Tests`' collections wait for inside one process is not found; attaching a profiler
  (`dotnet-stack`) was stopped by the permission check. The user can allow it, or run it by hand.
- **A flaky gateway test:** `ClientAddressTests.Forwarded_address_is_used_only_through_a_trusted_proxy` failed in 2 of
  8 local runs of the gateway's `Gateway.Tests`. If a gateway CI run fails on it, re-run the job.

## How to

- **A release:** the preparation as #788 did it (*Unreleased* in `CHANGELOG.md` and `docs/upgrade-guide.md` under the
  version, `docs/portainer.md`'s version, the ticked queue items to `plans/archive/work/to-v<version>.md`, the
  roadmap's table; `scripts/unreleased.py fold` moves `changes/` in), a dry run with `e2e=true`, an annotated tag on
  the merge commit (with the user's go-ahead; pushed by `thekim1` or the user: release tags are protected, *Start
  here*), then the pin pull
  request that `release.yml` opens: it starts no checks, so close and reopen it before merging (handbook, *CI and
  releases*). The upgrade boot test starts from `release.yml`'s `UPGRADE_FROM` (0.6.0); its images must still be on
  ghcr.io.
- **Show a deploy change before a release:** a dry run from the branch (`gh workflow run release.yml --ref <branch>
  -f version=X.Y.Z-dryrun -f e2e=true`, about 30 minutes, with the runners idle). The boot-only run
  (`-f portainer_boot=X.Y.Z`) takes minutes but uses that version's images already on ghcr.io, so it only fits a change
  that needs no new image.
- **Rebuild the Portainer stack:** `sh deploy/portainer/build-stack.sh v0.12.2` (Fors's release; or its next one);
  `--check` is what `deploy-checks` runs.
- **Try Källa's wizard locally** (a throwaway volume each time): `export FakeGateway__Enabled=true
  Seed__Enabled=false Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false ClamAv__Mode=off
  DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire start …`, `aspire wait admin-ui`; the code
  is in `aspire logs adminapi | grep 'Setup code:'`; the provider is `https://localhost:7411/realms/fors`, the clients
  `kalla-admin` and `open-webui-dev` (secrets: the AppHost parameters `oidc-client-secret`,
  `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop --all`, wait, `docker volume rm`.
  Or run the spec: `tests/e2e/README.md`. On a FakeGateway stack the sign-in step shows the note that the gateway's
  sign-in is not set up yet (the fake announces the feature with no address).
- **Try the gateway's wizard locally:** in a gateway worktree, `Setup__Try=true Seed__Enabled=false
  DataApi__Enabled=false DevelopmentVolumes__Postgres=<a throwaway name>`, `aspire start --apphost
  src/Fors.AppHost/Fors.AppHost.csproj`, then `npx playwright test specs/setup.spec.ts` in
  `tests/e2e` (the gateway's `docs/local-development.md`, *Trying setup mode*). Port 5173 is taken here: set
  `ADMIN_UI_URL` to the port `aspire logs admin-ui` names and add that address's `/signin-oidc` to the `fors-admin`
  client in the bundled Keycloak.

## State

- **Release:** `v0.9.2` published 2026-11-21 23:27 UTC (tag at `c7d5de47`, on Fors `v0.12.2`); `v0.9.1` and `v0.9.0`
  the same day. All are `ghcr.io/thekim1/kalla/*`, public, Apache-2.0. `v0.8.0` and earlier are Ume.Rag
  (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2).
- **Audit:** last green on 2026-11-21 ([run 44905272366](https://github.com/thekim1/kalla/actions/runs/44905272366));
  0.9.2's images are scanned from the next run. The dated exceptions are in *Watch*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN`, no secret).
- **On `main`, unreleased** (eleven notes in `changes/`, one upgrade note: restart `kalla-proxy`): assistant
  templates, copy an assistant, the version diff and test questions per assistant (B-54 to B-57); a source's own
  schedule and its next run (#935); the TLS proxy's header buffers (#944); the organisation's logo from Fors and the
  favicon (#953); one path-rule matcher and one schedule rule in `Kalla.Pipelines` (A-42, A-43); the code-quality
  refactors (#964 to #973, #981). Plan files: #961, #975, #976, #982, #985, #988, the two proposals.
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open, and issue
  #974, #987; A-42 and A-43 are done. Stream b: B-58, B-59 and B-63 merged (#977, #984, #978, #963); #983 (release
  blocker) first, then B-61, B-62 and B-66 (F10, F7, F6), B-64 and B-65 (the topics editor),
  B-67 to B-69 (P2 wave 2), B-70 and B-71 (a source's pause and next run), B-72 and B-73 (the chat extension), B-74 to
  B-82 (the UX pass and the Publish dialog; B-74 waits on nothing), B-83 (replace an upload by name, before T4; waits
  on nothing), B-42 to B-46 (the gateway phases' live checks) and
  its live items. An item someone has started says **Taken** in the queue. Main: M-4.
- **Open issues that matter:** #983 (security, release blocker), #986 (a chat metric for to-verify #103), #987 (the
  grading script), #980 (code quality's tracker), #959 (the chat extension; closes with B-72 and B-73), #974 (code
  quality, stream a's part), #781 and #921 (watch), #920 (the VM's upgrade, `thekim1`'s), #929 (topics; closes with
  B-65), #667 (M-16's issue: the work is done, `thekim1`'s to close), #333, #511, #580 and #550. In Fors: no open pull
  request; proposals #108 and #109, #99, #91, and a flaky test #124.
- **Dependabot works:** grouped pull requests; `.github/dependabot.yml`'s header lists where a pin is repeated.
  Playwright is bumped by hand, in the package list, the renderer image and its lockfile together (#907).
- **The user's machine (`~/research/rag`), not the machine of session 56:** the main clone on `main`, no worktrees.
  Docker works without sudo; there is no sudo.
  - **The development stack is under the new names** (session 53; the old `ume-rag` data removed in session 54):
    secrets in `kalla-apphost-dev`, data in
    `kalla-dev-postgres`, `kalla-openwebui-data` and `kalla-clamav-db`. A start against a real gateway needs
    `Gateway:Services:<service>:Key` first; until then start with `FakeGateway__Enabled=true` (handbook, *Local stack*).
    Stop it with `aspire stop --apphost src/Kalla.AppHost/Kalla.AppHost.csproj` (the permission check stops `--all`).
  - **Local images:** `kalla.local/renderer:ci` (Playwright 1.65.1; the worker's render tests take it through
    `KALLA_TEST_RENDERER_IMAGE`).
  - **Docker's default address pools are full** (about 25 unused networks of other projects): a new Compose project
    needs explicit subnets, or a `docker network prune` by the user. The machine's name is upper case and does not
    resolve inside containers. Neither `gh` token has `read:packages`; Källa's images are public.
  - **Tools:** `kubectl` is installed (`kubectl kustomize` renders the overlays); `actionlint` is at
    `/snap/bin/actionlint`; `shellcheck` and `pwsh` are not installed; `grep` is ugrep. Playwright's Chromium and
    Firefox are in `~/.cache/ms-playwright`. The Aspire CLI is at
    `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine:** a fresh clone after the rename; its development data starts empty under `~/.kalla`.
- **Fors on the user's machine:** `~/git/llmgateway` is a clone of `thekim1/fors`, checked out at `v0.12.2` with
  `origin/main` fetched at `6c3c412` (#126, the logo endpoint); read a release with `git show v0.12.2:<path>`. The remote is still named `thekim1/llmgateway` there (GitHub redirects); push
  with the `gh` credential helper. Merges and releases in Fors are the user's; its rules: draft pull requests (drafts
  run no CI), checks run locally first, squash-merge. Ports 5000 and 5173 are taken here
  (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
