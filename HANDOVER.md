# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.10.0`** (2026-11-23, tag at `5a74afb8`, on Fors `v0.13.0`): P2 wave 1. **`main` has unreleased code
since the tag:** six stream-b items built on the user's machine in session 57 (*State*). The user's VM runs 0.5.0.
Three machines have worked on it: `~/research/rag` (the user's, sessions 49 to 55 and 57), `~/git/ume-rag-platform`
(the user's WSL machine, session 56) and `thekim1`'s (stream b, stream a, the releases, and a coordinating session
that writes proposals). Several sessions post as `ume-nichlas` at the same hour: look before you write. *Start
here* says what does not travel between machines. Branch `main`, remote `origin` =
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

## Start here (session 58)

**What does not travel between machines:**
- **Claude Code's own memory stays on the machine it was written on.** Everything that binds is in
  [`plans/work/main-handbook.md`](plans/work/main-handbook.md) (*How we work*); read it before anything else.
- **Accounts:** the session works as `ume-nichlas`, and approvals are the user's (`Crotalus`). Both accounts are in
  `gh` on `~/research/rag` and on `~/git/ume-rag-platform`; run `gh auth status` first.
- **Pushing:** on both of the user's machines `gh` is the credential helper (below); it works on
  `~/git/ume-rag-platform` too (GitHub answers with a "repository moved" note, harmless).
- **Fors:** Källa's contract copy is taken from Fors `v0.13.0` (`9dc9315`). `~/git/llmgateway` on
  `~/git/ume-rag-platform`'s machine was at `6c3c412` (one commit before that release; the contract is the same):
  fetch it there. Elsewhere clone https://github.com/thekim1/fors beside this repository.
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

**Open when this was written** (2026-11-24, about 00:30 UTC): `thekim1`'s #1024 (the Pause follow-ups of #1007,
reviewed: ready), #1025 (B-74, reviewed: ready), #1027 (B-75 part 1, reviewed: nine points, three before it merges)
and #1029 (B-75 part 2, **not reviewed yet**); Dependabot's #958; and the main session's own pull request with this
file, the handbook's lessons and the design deviations of session 57's six items. In order:

1. **#1029 (B-75 part 2, the names of admin §5.7)** has no review yet; it renames tabs, buttons and filters, so
   grep `tests/e2e/specs/` and `docs/` for every old name (the browser tests don't run on pull requests). **#1027's
   review** asked for three things before it merges: the phone's *Knowledge bases* tab on detail pages (merging
   #1025 first fixes it), the not-scanned summary's sentence (admin §5.5.7 point 4 now says "…were not scanned for
   malware: the scan was off when they were stored"; the code still has the old text) and the docs hand-off (filed
   since as #1030).
2. **Session 57's design deviations are in this file's pull request** (admin §5.5.7, §8.6, §8.7, §8.8, §5.5.1;
   chat §9.10, §12.2, §17, §18.9; data-model §8). Two things in it are still to build, neither queued yet:
   **the chat stage metrics of chat §17** (`rag.chat.stage.duration`, `rag.chat.ttft`, `rag.chat.overhead` were
   never emitted; B-72 takes its own timestamps), and **a filter for files not scanned for malware** in the file
   list (the row mark is gone with #1027).
3. **B-83's hand-off, stream a's:** `e2e.yml` runs only the `pilot` and `accessibility` projects; the new `upload`
   project (`tests/e2e/specs/upload.spec.ts`) runs by hand. Add it to the workflow, or decide it stays by hand.
4. **A paused upload source on 0.10.0** (said on #1024): 0.10.0 shipped *Pause* on upload sources, and
   `POST /api/upload-batches/{id}/commit` still takes a batch for a paused one, whose sync job the worker drops.
   #1024 stops new pauses and keeps *Resume*; refusing the commit with `source_paused` is open.
5. **The cards of session 57 and the UX pass:** B-78 moves *Content gaps* to the assistant's *Quality* tab and the
   *Cost* card's outcome tables to *Usage*; B-79 places *Feedback on documents* on the new Overview. The new cards
   print their counts without `quietZero` (#1027's rule 8): whoever lands second adds it. On the *Cost* card a cost
   below 0.00005 US$ reads "0,00 US$" (the existing `formatUsdCost`).
6. **#981's follow-ups** (on #981, for #980) and **#978's smaller points** (its review) are as handover 57 left
   them: `TestQuestionDrawer.vue`'s hand-rolled debounce, `AssistantTestsTab`'s fixed poll, `RunView` and
   `PublicationView` not reloading on a `props.id` change; weak tests for the key item on a knowledge base.
7. **#958, the parser's bumps:** `LICENSES.md` has to be regenerated on the branch; stream a's. Best taken with
   **opencv 5.1 from 2026-11-25** (*Watch*): try `opencv-python-headless` 5.1.0.95 once the parser's uv cutoff lets
   it in; it may retire the FFmpeg exceptions before 2026-12-17.
8. **The docs issues, stream a's:** #992 (instructions that fail: `KALLA_OTEL_ENDPOINT` does not exist, it is
   `FORS_OTEL_ENDPOINT`; `deployment/rag-*`; the runbook's Portainer backup list), #993 (stale facts), #994 (project
   vocabulary in operator pages), #995 (gaps: **Portainer backup and restore with a drill**, the nightly answer
   run, *Needs attention*, a new department, key expiry, an upgrade from 0.9.x), #1030 (the not-scanned mark) and
   #1028 (B-75's platform pages: Settings groups, Models tabs, *Apps and keys*, in `src/admin-ui/src/platform/**`).
9. **The Audit** was red on 0.9.2's images (#1009); #1010's fixes are in 0.10.0. Look at the first nightly run
   after the release: green closes #1009.
10. **#925's steps 2 to 4** (personal-data verdicts, *reject*, the false-positive summary): the Decisions entry and
    the design changes are `thekim1`'s session's (said on #925). Step 1 is B-63 (#963, merged).
11. **The VM's upgrade is `thekim1`'s** (#920): 0.5.0 straight to the current release. No report yet.
12. **Repository variables:** the workflows read `vars.KALLA_TEST_HOST_RUNNER`, `vars.KALLA_TEST_HOST_DEPLOY_ENV`,
    `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`, and none exist; `thekim1` was asked on #905 (2026-11-21), no
    answer yet.
13. **#1015** (the release's merge hold) is still open though `v0.10.0` is tagged and published: `thekim1`'s to
    close.

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

## What session 57 did (2026-11-23)

On `~/research/rag` (the user's machine), as the main session, while `thekim1`'s machine released 0.10.0. Session
56's work is in this file's history.

- **Six stream-b items, taken on the user's word and announced in #1011** (closed): **B-83** replace an upload by
  name (#1017), **B-61** cost per outcome (#1018), **B-72** progress chunk and timings (#1021), **B-62** content-gap
  report (#1022), **B-66** downvotes per document (#1023) and **B-73** topics and the nearest subject (#1026). One
  implementor agent per item in its own worktree; each diff read against the design, its tests re-run, the three
  cards and the upload dialog checked in a browser on a FakeGateway stack; all merged, #1022 by `thekim1`.
- **Found in review and fixed before the pull request:** the content-gap report kept naming another department's
  knowledge base after its sharing was withdrawn (now #983's rule: named only while linkable today); B-66's table
  had no retention (now 3 years, as its sibling tables).
- **Approved as `Crotalus` on the user's word and merged:** #1008, #1004, #1010, #1012, then #1017, #1018, #1021,
  then #1023, #1026.
- **Reviews as comments:** #1019 (the release's browser-test fix), #1024, #1025, #1027 (with a review agent; nine
  points, each checked in the files).
- **Kept off the release:** nothing of this went to `main` between #1015's hold and the tag; #1018's `Verify` run
  was cancelled while the dry run ran and ran again after it.
- **This file's pull request:** the design deviations of the six items, the handbook's lessons (a force push is
  stopped; a table-adding stream-b item needs a `main-session/` branch; stacking on an unmerged branch; agents for a
  lane and for browser checks; port 5174) and this handover.

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
- **Rebuild the Portainer stack:** `sh deploy/portainer/build-stack.sh v0.13.0` (Fors's release; or its next one);
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

- **Release:** `v0.10.0` published 2026-11-23 21:02 UTC (tag at `5a74afb8`, on Fors `v0.13.0`; the Portainer stack
  is pinned to its images since #1020). `v0.9.x` are `ghcr.io/thekim1/kalla/*` too, public, Apache-2.0. `v0.8.0` and
  earlier are Ume.Rag (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2).
- **Audit:** red on 2026-11-22 and 2026-11-23 on 0.9.2's postgres, clamav and parser images
  ([run 45197573594](https://github.com/thekim1/kalla/actions/runs/45197573594), #1009); 0.10.0 has the fixes
  (#1010) and is scanned from the next run. The dated exceptions are in *Watch*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN`, no secret).
- **On `main`, unreleased** (six notes in `changes/`, no upgrade note; three migrations, each adding one `chat`
  table with its grants: `AssistantDailyOutcomes`, `AssistantDailyGaps`, `DocumentDailyFeedback`): cost per outcome
  on the *Cost* card (B-61), the content-gap report (B-62), downvotes per document with its attention item (B-66),
  `kalla.progress` and `kalla.timings` (B-72), `topics` on related documents and `kalla.nearest` (B-73), an upload
  with a known name replaces the file (B-83).
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open, and its
  issues (*Start here*, 3, 7 and 8, and #974, #987). Stream b: the UX pass, in `thekim1`'s hands (B-74 #1025, B-75
  #1027 and #1029, then B-76, B-82, B-77, B-78, B-79, B-80, B-81; B-78 and B-79 no longer wait on anything of
  session 57's), B-64 and B-65 (the topics editor), B-67 to B-69 (P2 wave 2), B-71, B-42 to B-46 (the gateway
  phases' live checks) and its live items. An item someone has started says **Taken** in the queue. Main: M-4.
- **Open issues that matter:** #992 to #995, #1028 and #1030 (the docs and the platform pages, stream a's), #1009
  (the Audit), #1007 (closes with #1024), #987 (the grading script), #980 (code quality's tracker), #974 (code
  quality, stream a's part), #781 and #921 (watch), #920 (the VM's upgrade, `thekim1`'s), #929 (topics; closes with
  B-65), #667 (M-16's issue: the work is done, `thekim1`'s to close), #1015, #333, #511, #580 and #550. In Fors: no
  open pull request; proposals #108 and #109, #99, #91, and a flaky test #124.
- **Dependabot works:** grouped pull requests; `.github/dependabot.yml`'s header lists where a pin is repeated.
  Playwright is bumped by hand, in the package list, the renderer image and its lockfile together (#907).
- **The user's machine (`~/research/rag`), not the machine of session 56:** the main clone on `main`, no worktrees
  (session 57's are removed). Another project's Vite server held port 5174 on 2026-11-23 (handbook, *Environment
  notes*).
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
- **Fors on the user's machine:** `~/git/llmgateway` is a clone of `thekim1/fors` on `main` at `v0.13.0`
  (`9dc9315`); read a release with `git show v0.13.0:<path>`. The remote is still named `thekim1/llmgateway` there
  (GitHub redirects); push with the `gh` credential helper. Merges and releases in Fors are the user's; its rules:
  draft pull requests (drafts run no CI), checks run locally first, squash-merge. Ports 5000 and 5173 are taken here
  (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
