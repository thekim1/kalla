# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.9.2`** (2026-11-21, tag at `c7d5de47`, on Fors `v0.12.2`). **`main` has unreleased code since
2026-11-23:** P2 wave 1's first four items, a source's schedule, the fix for the 502 after sign-in, the organisation's
logo and six code-quality refactors (*State*). The user's VM runs 0.5.0. Two machines have worked on it:
`~/research/rag` (the user's, where sessions 49 to 55 ran) and `thekim1`'s (stream b, stream a, the releases, and a
coordinating session that writes proposals). **Session 56 runs on another machine than session 55** (user,
2026-11-23): *Start here* says what does not travel. Branch `main`, remote `origin` =
https://github.com/thekim1/kalla.

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

## Start here (session 56)

**On a machine this work has not run on:**
- **Claude Code's own memory stays on the machine it was written on.** Everything that binds is in
  [`plans/work/main-handbook.md`](plans/work/main-handbook.md) (*How we work*); read it before anything else.
- **Accounts:** the session works as `ume-nichlas`, and approvals are the user's (`Crotalus`). Run `gh auth status`
  first; whether both accounts are in `gh` there is not known.
- **Pushing:** on `~/research/rag` a plain `git push` hung on the Windows credential manager, so `gh` was the
  credential helper (below). Another machine may need neither.
- **Fors:** clone https://github.com/thekim1/fors beside this repository to read the gateway's code at a tag or at
  `main` (`6c3c412` is the commit Källa's contract copy is pinned to).
- *State*'s machine block describes `~/research/rag` only.

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
- **Push with `gh` as the credential helper:** a plain `git push` here asks the Windows credential manager and hangs.
  Use `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' push …` (session 54).
- **Look at the open pull requests before each step** (`gh pr list`): the other machine works at the same hour. Say on
  its pull request what you are about to write; review its pull requests as comments (claims checked against the code).
- **A decision for the user is asked as four alternatives with one recommendation** (the question tool).

**Open when this was written** (2026-11-23, about 11:00 UTC): `thekim1`'s #978 (B-59, just opened, not read), #977
(B-58), #963 (B-63), Dependabot's #958 and this file's pull request. In order:

1. **#977, B-58 the nightly answer run:** reviewed as a comment, not approved. It follows #961's design. To fix:
   - **an archived assistant still gets a run every night** (the service takes every assistant with a published
     version; archiving keeps it): add `State == active`, and the main session writes "published and active" into
     processing §14.8;
   - **the exception's message is stored in the judge's note**; a gateway client's message can name the gateway's host
     (#232: type and status only, `GatewayModelSync.ErrorText`). The same two lines are on `main` in
     `AssistantTestRunner.cs:297` and `:322` (B-57), where the note **is** shown to department users;
   - smaller: no run without a judge model (today it pays for the answers and judges nothing); an `.upgrade.md` that
     says the run is on by default.
2. **#978, B-59 Home's *Needs attention*:** not read. Review it against admin §5.5.1's table (#961): the twelve items
   marked *the list*, B-57's item (merged), and B-58's once #977 is in.
3. **#963, B-63 clear a personal-data hold:** reviewed, not approved. It needs `@cleared="documents.reload()"`
   (#964 removed `loadDocuments`), a rebase, and the fault from the review: a document flagged again after a clearance
   gets no *Clear*, because both views hide the actions once `PiiClearedAt` is set and nothing ever unsets it (the
   Documents list needs a field for "a flagged set is held"). The code-quality items #5 and #3 wait for it.
4. **#958, the parser's bumps:** `LICENSES.md` has to be regenerated on the branch (the docstring of
   `src/parser/scripts/licences.py`); stream a's. Best taken with opencv 5.1 (item 9).
5. **Design debts of the main session:** deviations of merged pull requests that no design records yet.
   - **#977, once merged:** the answer call's step `eval-answer-chat`; what `Found`, `Wins` and `Losses` mean for an
     `answers` run; the question, answer and note stored on its results (data-model §2.2 says `assistant-tests` only);
     the metric `rag.admin.answer_runs`.
   - **#956 (B-57):** the judge's own step `assistant-test-judge`; the cost shown as model calls, not in USD and not
     in the Publish dialog; the document and phrase checks stored as one `Hit`; *Keep* stores nothing.
   - **#952 (B-55):** *Copy* is a button (until the UX pass's ⋯ menu); the drawer asks for name and department only;
     the source's own fields (description, contact, class, history, retention) are copied.
   - **#954 (B-56):** no skills in the diff yet; the general Publish dialog's diff is not built; the bound of 2,000
     differing lines.
   - **#950 (B-54):** template fields are checked for shape only. (Its seeding deviation is settled, #961.)
6. **The code of B-54 to B-57 has no main-session review.** `thekim1` merged #950 to #956 within the hour. Only B-57's
   retention was read. Their security-relevant parts (the roles and scope checks of the new endpoints: templates,
   copy into another department, the diff, test questions and test runs) are worth one read.
7. **The UX pass's six decisions** (UX1 to UX6, [`plans/proposals/admin-ui-ux-pass.md`](plans/proposals/admin-ui-ux-pass.md),
   merged as a proposal in #955): the user's, each with four options and a recommendation in the proposal. The main
   session agreed with all six in its review, with UX2 flagged (it undoes the *Files* tab of 2026-11-09). Once picked,
   the rules go into Decisions "Admin API and UI" and admin §5. Its step 0 (the phone's bottom menu) needs no decision.
8. **The code-quality plan** ([`plans/proposals/code-quality-top7.md`](plans/proposals/code-quality-top7.md), #960,
   asked for by the user): done are #1 (#964), #2 (#966, #971), #4 (#965), #6 (#968) and #7 (#967, #973); #5 and #3
   wait for #963; stream a's parts are issue #974.
9. **opencv 5.1 from 2026-11-25** (*Watch*): try `opencv-python-headless` 5.1.0.95 in the parser once its uv cutoff
   lets it in; it may retire the eleven FFmpeg exceptions before 2026-12-17.
10. **#925's steps 2 to 4** (personal-data verdicts, *reject*, the false-positive summary): the user answered the four
    questions (decisions log, 2026-11-21); the Decisions entry and the design changes are `thekim1`'s session's (said
    on #925). Step 1 is B-63 (item 3).
11. **A full read of design shares 2 to 4** once agents can run again (the usage limit resets 2026-11-26 19:00,
    Europe/Stockholm). Not read at all: `plans/to-verify.md` beyond the rows #941 touched, `postponed-decisions.md`
    beyond its P2 header, `plans/cases/support-assistant/`, `plans/gold-set/README.md`, `docs/` (stream a's).
12. **The VM's upgrade is `thekim1`'s** (#920): 0.5.0 straight to 0.9.2 (`docs/portainer.md`, *Upgrade*). A step that
    did not go as the guide says is a guide fix; the roadmap's T1 row follows the result. No report yet.
13. **Repository variables:** the workflows read `vars.KALLA_TEST_HOST_RUNNER`, `vars.KALLA_TEST_HOST_DEPLOY_ENV`,
    `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`, and none exist; `thekim1` was asked on #905 (2026-11-21), no
    answer yet.
14. **Before Källa's next release, Fors needs one.** `contracts/gateway/` is pinned to Fors `main` at `6c3c412`
    (thekim1/fors#126, "no release yet"), and the logo of #953 shows only on a Fors that has it. Then the contract
    README's *Taken from* names the release and the Portainer stack pins it. Fors merges and releases are the user's.

### Waiting on the user

- **UX1 to UX6** (item 7).
- **The umea.se inventory (A-L1):** the first input of the live block (`plans/roadmap.md`); every live item waits on an
  input, A-L1 first.
- **The NVDA pass (to-verify row 96):** the user's team runs it on Windows with `plans/work/nvda-pass.md`. Its
  checklist names today's tabs, so hold it until the UX pass's UX2 and UX3 have landed and rewrite the checklist once
  (the proposal's *Hand-offs*); the result goes in `results-main.md`.
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

## What session 55 did (2026-11-22 to 2026-11-23)

On `~/research/rag`, as the main session. Session 54's work is in this file's history.

- **Read P2 wave 1 and 2's designs against the code before stream b built them**, and found gaps. **#961:** Decisions
  "Nightly answer run" (two of the user's decisions: `Settings.EvaluationAnswerQuestions`, 0 to 100, default 100, 0
  switches the run off; the attention item at 5 points below the 28-day median), how an `answers` run is keyed and
  kept, admin §5.5.1's attention items as a table of what builds each, and one seeding rule for assistant templates.
- **#959 answered.** The user adopted both parts. **#975:** Decisions "Progress, timings and the nearest subject in
  the chat extension", chat §9.10 and §9.11, B-72 and B-73. One change to the proposal: the progress chunk is written
  after the gateway has accepted the answer call, so a refused budget or key stays an HTTP error.
- **#976:** identity §1.5 has `logoUrl` and the anonymous logo endpoint as #953 built them.
- **Reviews as comments,** each claim checked in the code: #963, #953 with thekim1/fors#126, #955, #960, #957, #958,
  #964 to #973, #977. Taken in by the authors: a logo read can never fail `/bff/user` and a wide logo keeps its shape
  (#953); Home keeps its checklists and notices, the assistant gets a *Quality* tab (#955); the permission matrix as a
  contract file instead of a third hand copy (#960); B-57's retention in its own migration, not a stream-a hand-off
  (#956).
- **Approved as `Crotalus` on the user's word and merged:** #961, #957, #955, #960, #964 to #967; then #953, #968,
  #970, #971, #972, #975; then #976, #969, #973.
- **`~/research/rag`:** the main clone on `main`, no worktrees, the session's temporary refs removed.

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
- **On `main`, unreleased** (ten notes in `changes/`, one upgrade note: restart `kalla-proxy`): assistant templates,
  copy an assistant, the version diff and test questions per assistant (B-54 to B-57); a source's own schedule and
  its next run (#935); the TLS proxy's header buffers (#944); the organisation's logo from Fors and the favicon
  (#953); one path-rule matcher and one schedule rule in `Kalla.Pipelines` (A-42, A-43); the code-quality refactors
  (#964 to #973). Plan files: #961, #975, #976, the two proposals.
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open, and issue
  #974; A-42 and A-43 are done. Stream b: B-58 (#977) and B-59 (#978) in review, B-63 (#963) in review, then B-61,
  B-62 and B-66 (F10, F7, F6), B-64 and B-65 (the topics editor), B-67 to B-69 (P2 wave 2), B-70 and B-71 (a source's
  pause and next run), B-72 and B-73 (the chat extension), B-42 to B-46 (the gateway phases' live checks) and its
  live items. An item someone has started says **Taken** in the queue. Main: M-4.
- **Open issues that matter:** #959 (the chat extension; closes with B-72 and B-73), #974 (code quality, stream a's
  part), #781 and #921 (watch), #920 (the VM's upgrade, `thekim1`'s), #929 (topics; closes with B-65), #667 (M-16's
  issue: the work is done, `thekim1`'s to close), #333, #511, #580 and #550. In Fors: no open pull request; proposals
  #108 and #109, #99, #91, and a flaky test #124.
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
