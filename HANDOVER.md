# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`). **Last
release `v0.11.0`** (2026-11-25 00:19 UTC, tag at `a3076ed4`, on Fors `v0.13.0`): the admin UI reworked (the UX pass
B-74 to B-82), B-64, and session 57's six items. **`main` has unreleased code since the tag:** P2 wave 2 (B-67 to
B-69), B-65, B-71, all of #1101's Compose work (D1 to D8), #1084's faster CI (R1 to R7), the operator docs and the
review follow-ups (*State*); **0.12.0 has the user's go-ahead** (#1136). The user's VM was upgraded by `thekim1`
(#920). Three machines have worked on it: `~/research/rag` (the user's, sessions 49 to 55 and 57),
`~/git/ume-rag-platform` (the user's WSL machine, session 56, which ran beside session 57 until 2026-11-24) and
`thekim1`'s (stream b, stream a, the releases, and a coordinating session that writes proposals). Several sessions post
as `ume-nichlas` at the same hour: look before you write. *Start here* says what does not travel between machines.
Branch `main`, remote `origin` = https://github.com/thekim1/kalla.

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

## Start here (session 62)

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

**Open when this was written** (2026-11-27, morning): the main session's #1133 (the user's answers of 2026-11-26,
**approval needed**) and this file's pull request; Dependabot's #958. The *Protect main* ruleset is fixed (#1122,
`changes, ownership, doc-refs` required, no `evaluation-smoke`), so approved pull requests merge normally again.
`thekim1` merges its own with `--admin`, often within the hour; **review only open pull requests** (handbook, *How we
work*). In order:

1. **#1133** (approve, then merge): P2 wave 3; the empty answer retried then `unavailable` (chat §6.3, #1106); the
   backup attention items (admin §5.5.1); #1101's two accepted deviations in Decisions "Compose host layout and
   upgrades" (secrets and certificates stay in volumes; the odd-host test upgrades from the previous release).
2. **Release 0.12.0, go-ahead given (#1136, `thekim1`):** the preparation pull request (review it while it is open:
   the fold, `CHANGELOG.md`, the upgrade guide's 0.12.0 section **with the Compose state-directory move to
   `/etc/kalla` as an operator step**, `docs/portainer.md`'s version, `to-v0.12.0.md`, the roadmap's table), its
   squash merge, then the dry run **on `main`** with `e2e=true` (runners idle; the last `e2e.yml` run was 2026-11-23),
   the tag on **that same commit**, the pin pull request. The tag run's gate should skip verify and parser and name the
   dry run; its time goes on #1084 against the 11.3-minute target.
3. **P2 wave 3 (#1135):** each stream writes its queue items from the designs and posts the list there. Stream b:
   Tools and MCP (tools design §3 to §8, read-only, `client-credentials`; M1 and V1's re-checks first). Stream a:
   Ingestion breadth (#511's worker split first, then site-profile learning, e-mail and archives, the shared frontier,
   effect-tag publish plans). Answer design questions there; review the first pull requests of each while open.
4. **Stream b's decided items:** #1106 (the empty answer: one retry with the full limit and the lowest reasoning
   effort, then `unavailable`, three test cases) and #1134 (the backup items; the admin's read path to the backup
   status is a question for stream a on that issue). Flaky: #1115.
5. **Fors #143** (`upgrade-to-0.12.sh` reading the deployment as Källa's `deploy/upgrade/lib.sh` does): the gateway's.
6. **The NVDA pass** can start (`plans/work/nvda-pass.md`, rewritten in #1061); the user's team runs it.
7. **Not queued yet, decide and queue** (design is on `main`): **the chat stage metrics of chat §17**
   (`rag.chat.stage.duration`, `rag.chat.ttft`, `rag.chat.overhead`, marked not built), and **a filter for files not
   scanned for malware** in the file list (the row mark is gone since #1027).
8. **B-83's hand-off, stream a's:** `e2e.yml` runs only the `pilot` and `accessibility` projects; the `upload` project
   runs by hand. Add it to the workflow, or decide it stays by hand.
9. **A paused upload source** (said on #1024): `POST /api/upload-batches/{id}/commit` still takes a batch for a paused
   one, whose sync job the worker drops; refusing it with `source_paused` is open.
10. **#1084:** P7 (the parser-image ordering) is to be measured once on a parser pull request; drop it from the record
    if it saves under half a minute.
11. **#958, the parser's bumps:** `LICENSES.md` regenerated on the branch; stream a's. Try `opencv-python-headless`
    5.1.0.95; it may retire the FFmpeg exceptions before 2026-12-17.
12. **Docs still open, stream a's:** #993 (stale facts), #995 (the rest after #1100).
13. **#925's steps 2 to 4** (personal-data verdicts, *reject*, the false-positive summary): `thekim1`'s session's.
14. **Repository variables:** `vars.KALLA_TEST_HOST_RUNNER`, `vars.KALLA_TEST_HOST_DEPLOY_ENV`, `vars.KALLA_R1_*`
    and `secrets.KALLA_R1_API_KEY` don't exist; asked on #905 (2026-11-21), no answer yet.

### Waiting on the user

- **The umea.se inventory (A-L1):** the first input of the live block (`plans/roadmap.md`); every live item waits on an
  input, A-L1 first.
- **The NVDA pass (to-verify row 96):** the user's team runs it on Windows with `plans/work/nvda-pass.md`, rewritten
  for 0.11.0 (#1061); the result goes in `results-main.md`.
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

- **Never commit a `node_modules` symlink.** #1047's merge of `main` into its branch added
  `src/admin-ui/node_modules` as a link to its author's checkout (`.gitignore`'s `node_modules/` matches directories
  only; #1048 adds `node_modules`). A pull then deletes the local packages, since git treats ignored files as
  expendable, and stops half-way: compare the leftovers with `origin/main`, finish the fast-forward, `npm ci`. A
  worktree that needs packages gets its own `npm ci`, not a link.
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

## What session 60 did (2026-11-25 to 27)

On `~/research/rag`.

- **Post-merge reviews** (a review agent each, the main findings checked in the code before posting): #1060 → #1075;
  #1068 and #1071 → #1076; #1072 (comment); #1066 (checked); #1080 before its merge (comment, its one blocker fixed);
  #1074 → #1083; #1078 (comment); #1088 (comment); #1090, #1099, #1103 → #1108. Stream b fixed #1075, #1076 and #1083
  within hours (#1080, #1086).
- **The user's decisions** (each asked as four alternatives, the recommended one taken):
  - topics: rules from the sections found are host-first, a removed topic shows *Pending removal* (#1081, built in
    #1087);
  - #1084, CI time: the dry-run reuse (R6) with a release-only registry cache (R4a) on top of the quick wins; the
    low-risk pull-request set, not P4; stream a builds (#1107);
  - #1101, Compose hosts: state in `/etc/kalla`, a host-paths overlay with one `KALLA_DATA_ROOT`, upgrade scripts that
    read the deployment, `doctor.sh`, one combined odd-host upgrade test in the dry run; all of D1 to D8, stream a
    builds (#1109).
- **#1073** (#1069): Syft's temp on disk, its errors in the log, a leftover scan stopped. #1077, this file's previous
  version.
- Found the stale ruleset after #1090 (item 1). GitHub's API failed on writes for a few minutes (502s); #1109 was
  created through the REST API afterwards.
- **2026-11-26:** the ruleset fixed by `thekim1` from #1122's instructions (the main session has no admin on the
  repository); #1119 (the Decisions bullet typo; HANDOVER's release steps: the dry run on `main` after the squash
  merge, the tag on that commit, as #1114's gate needs). Reviews of the then-open #1118 (fixed before its merge),
  #1114 → #1120 and #1116 → #1121 (both fixed in #1124, #1125). Stream a built all of #1101 (D1 to D8, #1116 to
  #1132); #1101 closed with its two deviations accepted, Fors #143 opened for the gateway's upgrade script.
- **The user's answers of 2026-11-26** (four questions, the recommended option each): wave 3 tools plus ingestion,
  0.12.0 now, the empty answer retried then `unavailable`, the backup attention items (#1133, #1134, #1135, #1136).
- **The user's working rule from then on:** no reviews of merged pull requests (handbook, *How we work*).

## What session 59 did (2026-11-24 and 25)

On `~/research/rag`.

- **Post-merge reviews** (a review agent per pull request; each main finding checked in the code before posting):
  #1044 and #1046 → #1049; #1047 → #1051; #1056 → #1062; #1053 → #1063 (all fixed by `thekim1` within hours, in
  #1057, #1058 and #1072); #1059 (comment, points 1 and 2 fixed in #1066, checked); #1060 → #1075; #1068 and #1071 →
  #1076; #1072 (comment).
- **#1047 committed `src/admin-ui/node_modules` as a symlink** to its author's checkout. Pulling it deleted this
  machine's packages; #1048 removed it and ignores a `node_modules` link (*Watch*).
- **#1052:** the admin design took the day's deviations (§5.7's own · shared, §5.12's scroll padding and filters,
  the assistant's one-turn test panel, tabs without counts).
- **#1061:** the NVDA checklist for 0.11.0's admin UI (#1040).
- **#1073:** `release.yml`'s Syft fix for #1069 (open).
- Closed #1015 (the 0.10.0 merge hold) and #1009 (the Audit, green again).

## What session 56 did after #989 (2026-11-23 evening and 2026-11-24 morning)

On `~/git/ume-rag-platform` (the user's WSL machine), beside session 57. Its first part (the UX decisions, the
security read of B-54 to B-57, the reads of the never-read documents) is in #989's version of this file.

- **Reviews as comments** (a review agent per pull request, the main claims checked in the files, the touched tests
  run): #996, #997 (after its merge; the narrower read became #998, built in #1013), #999 (#983's fix), #1000, #1001,
  #1002 (merged before the review: its points became #1003, fixed in #1006 and reviewed after its merge), #1005
  (merged before the review: #1007), #1010, #1013, #1014 and #1041.
- **The user's decisions, recorded:** a paused source's header (*Resume* primary, *Fetch now* and *Continue*
  hidden) and no *Pause* on an upload source (#1004, built in #1024); Feedback's list and summary for its four
  roles in the API (#1038, built in #1014); the estimate ratio by answer model (chat §17, #991, built in #1000).
- **Design:** #991 (B-58, B-59, B-63 and #999's deviations), #1004 (the test-run cap, an unfinished test run after a
  restart, pausing a source), #1038.
- **Filed:** #1009 (the red Audit; fixed by #1010 in 0.10.0).
- **Approved as `Crotalus` on the user's word and merged:** #989, #990, #991, then #1038 and #1037 (brought up to date
  on the user's request).
- **A lesson:** `thekim1` merges its own pull requests within minutes of opening them. A review that arrives after the
  merge goes into a follow-up issue (#998, #1003, #1007); say on the pull request at once that a review is coming
  (#1041) so the merge can wait for it.

## What session 57 did (2026-11-23 and 24)

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
- **#1031:** the design deviations of the six items (admin §5.5.7, §8.1, §8.6, §8.7, §8.8, §5.5.1, §4.5; chat
  §9.10, §12.2, §17, §18.9; data-model §8), the handbook's lessons (a force push is stopped; a table-adding stream-b
  item needs a `main-session/` branch; stacking on an unmerged branch; agents for a lane and for browser checks;
  port 5174) and the roadmap. Approved on the user's word and merged.
- **After the overnight merges (2026-11-24 morning):** #1029 reviewed after its merge, follow-ups filed as #1036;
  the stack #1032, #1033, #1035 reviewed as comments (a review agent, the main points checked in the files).
- **The session's model** went from Fable to Opus 5.5 when Fable's usage limit was reached (2026-11-24); a resumed
  agent keeps the model it started on, so a stopped agent had to be started again.

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
  roadmap's table; `scripts/unreleased.py fold` moves `changes/` in); **after it is squash-merged**, the dry run on
  `main` itself (`gh workflow run release.yml --ref main -f version=X.Y.Z-dryrun -f e2e=true`), then an annotated tag
  on **that same commit**, with nothing merged in between: the tag run's `gate` matches the dry run by commit, and a
  dry run on the release branch never matches the squash commit (0.11.0's ran on `3b8f85f6`, its tag is `a3076ed4`;
  #1114's review). With the user's go-ahead; pushed by `thekim1` or the user: release tags are protected, *Start
  here*. Then the pin pull
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

- **Release:** `v0.11.0` published 2026-11-25 00:19 UTC (tag at `a3076ed4`, on Fors `v0.13.0`; the Portainer stack
  is pinned to its images since #1070). `v0.9.x` and later are `ghcr.io/thekim1/kalla/*`, public, Apache-2.0.
  `v0.8.0` and earlier are Ume.Rag (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2).
- **Audit:** green on 2026-11-24 (run 45419469305); #1009 closed. The dated exceptions are in *Watch*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN`, no secret).
- **On `main`, unreleased:** P2 wave 2 (B-67 the drawn Map, B-68 unused assistants with *Archive*/*Keep*, B-69 the
  evaluation trend chart), B-65 (the topic preview and form), B-71 (the admin's next run from the worker's rule),
  host-first topic rules and *Pending removal* (#1087), all of #1101 for Compose hosts (`deploy/doctor.sh`, the
  upgrade page, the units from `/etc/kalla/host.env`, `deploy/upgrade/lib.sh`, `compose.host-paths.yaml` and
  `--data-root`, the env file and the gateway's CA in `/etc/kalla`, the odd-host upgrade in the dry run), #1084's
  release gate, registry cache, parallel builds and boot tests and one guard job, the shared components copied back
  from Fors (#1123), the operator docs and the review follow-ups. Migrations: `Assistants.UnusedKeptUntil` (B-68).
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open, #993, #995,
  and wave 3's Ingestion breadth once queued (#1135). Stream b: #1106, #1134, #1115, and wave 3's Tools and MCP once
  queued; B-42 to B-46 wait on live checks, the B-L items on the gold set. Main: M-4.
- **Open issues that matter:** #1136 (0.12.0), #1135 (wave 3), #1134 and #1106 (stream b, decided), #1084 (P7 and
  the timing left), #1115 and #781 (flaky), #993, #995 (docs), #511 (the worker split, wave 3's first step), #333,
  #100 (the grading script for the go-live gate), #48. In Fors: #143 (the upgrade script), proposals #108 and #109,
  #99, #91, a flaky test #124.
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
