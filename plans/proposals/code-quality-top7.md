# Code quality: seven DRY and best-practice fixes

**Status:** **plan, being built** (user, 2026-11-23: "find the top 7 improvements for best practices and DRY in both
frontend and backend … and start implementation"). Written by the main session, 2026-11-23, from a read-only audit of
`main` at `9f48c107`; every reference below was opened, and the main session re-checked a sample (the four
`MaxLimit` constants, the 21 inline role lists, the literal gateway header names, the ten copies of the keyset
pattern, the four `ListLogger`s, the two identical `Enabled` properties). Each fix is one pull request on a
`main-session/cq-<n>-<slug>` or `b/cq-<n>-<slug>` branch (see *Order*). None changes behaviour visible to users, except two latent bugs that #1 and #5
fix. No file marked `unchanged` in `src/admin-ui/design-system.lock` is touched.

## What is already good

Logging is `[LoggerMessage]` throughout; faults map to problems in one place (`ApiFaultException`, `AdminFaults`); the
admin UI has `useLoadable` and `useFormErrors`. The findings are mostly abstractions that exist but are only half
used.

## The seven

| # | Fix | Area (owner) | Size | Main gain |
|---|---|---|---|---|
| 1 | `useKeysetList` for every *Load more* list | admin UI (b) | ~11 files, −60 net | One race guard; fixes a stale cursor and swallowed load errors |
| 2 | One paging kit in the admin API | AdminApi (b) | ~9 files, −10 net | One `MaxLimit`, one `take + 1` rule, one `LIKE` escape |
| 3 | Permissions by policy in the admin UI | admin UI (b) | ~16 files | The UI follows `PermissionMatrix` instead of 21 hand-written role lists |
| 4 | The gateway's step, run-id and pii headers named once | Answering, Retrieval, Infrastructure (b) | ~6 files | No literal `x-fors-*` strings; one `WithCallTag` |
| 5 | `usePoll` and `useDebouncedWatch` | admin UI (b) | ~9 files, −20 net | One backoff poll; no timers firing after a page is left |
| 6 | Shared run machinery for the two gateway syncs | AdminApi (b) | 3 files + `sql-shapes.json`, −80 net | A fix to one sync is no longer copied by hand to the other |
| 7 | Shared test doubles in `Kalla.TestKit` | tests (b) | ~14 files, −110 net | One counting, recording and stub handler and one `ListLogger` |

### 1. `useKeysetList` for *Load more* lists (admin UI)

**Now.** `next.value = page.next; return [...previous, ...page.items]` in ten places: `views/AuditView.vue:124`,
`views/FeedbackView.vue:68`, `views/RunView.vue:75`, `views/SourceView.vue:157`, `views/CollectionView.vue:150`,
`components/evaluation/EvaluationQuestions.vue:61`, `components/sources/HeldDocuments.vue:28`,
`components/sources/DeletionDecision.vue:31`, `components/sources/SourceQuarantine.vue:21`,
`components/files/FilesPanel.vue:93`. Four add their own request counter on top of `useLoadable`'s guard; `RunView`
and `SourceView` have none, so an older response can overwrite `next` (**a latent stale-cursor bug**); the three
source components `.catch(() => null)`, so a failed load never shows.

**Change.** `composables/useKeysetList.ts`, built on `useLoadable` with `{ items, next }` as one loaded value, so the
cursor only ever comes from the newest call; `{ items, next, loading, error, loaded, reload(), more() }`. The nine
simple sites move to it; their counters go. `FilesPanel`'s prefetch loop stays unless it fits cleanly.

**Proof.** `npm run test`, `npm run typecheck`, `npm run lint` in `src/admin-ui`; a new spec where a slow first call
resolves after a fast second one and `next` stays the second's; the existing view specs unchanged.

### 2. One paging kit in the admin API

**Now.** `MaxLimit = 200` four times (`AuditEndpoints.cs:16`, `DocumentEndpoints.cs:16`, `FeedbackEndpoints.cs:18`,
`RunEndpoints.cs:14`), borrowed by `UploadEndpoints.cs`, hard-coded in `EvaluationEndpoints.cs:63`; the
`Take(take + 1)` / next-cursor rule written ten times; `KeysetPage` in `RunEndpoints.cs`, `PageQuery`, `PageDto` and
`Like` in `CollectionEndpoints.cs`; `LIKE` escaping re-implemented inline in `EvaluationEndpoints.cs:54`.

**Change.** `src/Kalla.AdminApi/Paging.cs` holds `KeysetPage`, `PageQuery`, `PageDto`, `Like`, plus
`Keyset.Take(int? limit, int fallback = 50)` with the one `MaxLimit`, `KeysetPage.From(rows, take, key)` and
`Like.Contains(q)`. Every site moves; the constants go. **`AssistantEndpoints.cs` is left alone** until #952 and #954
have merged (both change it).

**Proof.** `dotnet test tests/Kalla.AdminApi.Tests`; new unit tests for `KeysetPage.From` at the page boundary and
`Like.Contains` with `%`, `_` and `\`.

### 3. Permissions by policy in the admin UI

**Now.** 21 inline `auth.hasAnyRole([...])` lists in 14 files (e.g. `views/CollectionView.vue:85`,
`views/SourceView.vue:48`, `components/sources/PipelineCard.vue:32`, `components/runs/CancelRunButton.vue:24`), while
`router/nav.ts` already has `MANAGE_ROLES`, `PLATFORM_ADMIN`, `FEEDBACK_ROLES` and the API decides by
`src/Kalla.Domain/Admin/PermissionMatrix.cs`. A role that gains or loses a policy needs 21 hand edits, and the UI can
drift from what the API allows.

**Change** (review on #960: no hand-written copy of the matrix). The matrix becomes a contract:
`tests/Kalla.AdminApi.Tests/PermissionMatrixTests.cs` emits `contracts/admin/v1/permissions.json` (policy → roles)
from `PermissionMatrix`, exactly as it already keeps `contracts/admin/v1/error-codes.json` (checked on every run,
rewritten with `KALLA_UPDATE_CONTRACTS=1`). The admin UI imports that file: `utils/permissions.ts` reads it,
`auth.can(policy)` in `stores/auth.ts` answers from it, the 21 inline lists are replaced (the assistant page's
included, now that #952 and #954 have merged), and the `router/nav.ts` constants are derived from it. A role that
gains a policy then fails a test on the side that wasn't changed. `platform/nav.ts` is stream a's and follows. Later,
and not needed for this: `/bff/user` returning the caller's policies (it touches #953's files).

**Proof.** The three admin-UI commands and `PermissionMatrixTests`; a Vitest spec that reads the contract file and
checks `can()` for every role and policy in it.

### 4. The gateway's headers named once

**Now.** `GatewayHeaders` (`Infrastructure/Gateway/GatewayOptions.cs`) lacks step, run id and pii, so
`Answering/Chat/GatewayChatClient.cs:177`, `Retrieval/ChatModelReranker.cs:79`, `Retrieval/GatewayReranker.cs:63` and
`Infrastructure/Gateway/ResidencyCeilingHandler.cs:59` write `"x-fors-step"` and `"x-fors-run-id"` as literals, and
`Answering/GatewayQueryEmbedder.cs:59` and `Worker/Processing/Embedding/EmbeddingClient.cs:34` define their own
constants. A typo silently loses cost and step attribution.

**Change.** `GatewayHeaders.Step`, `RunId`, `Pii`; `WithCallTag(step, runId)` beside `WithMaxResidency`; the stream-b
sites use them and `GatewayQueryEmbedder`'s constants go. `EmbeddingClient`, `EvalModelClient` and their tests are
stream a's: an issue for stream a. The FakeGateway keeps its own constants (it does not reference Infrastructure,
on purpose: it plays the gateway).

**Proof.** `dotnet test` on `Kalla.Api.Tests` and `Kalla.Retrieval.Tests`; a test of `WithCallTag`'s headers.

### 5. `usePoll` and `useDebouncedWatch` (admin UI)

**Now.** Hand-rolled debounce in `views/AuditView.vue:157`, `components/evaluation/EvaluationQuestions.vue:93`,
`components/evaluation/QuestionDialog.vue:62`, `components/files/FilesPanel.vue:218`; only `FilesPanel` clears its
timer on unmount, so **the other three can load after the page is left**. Five backoff polls: `useJobPoll.ts`,
`RunView.vue:82`, `SourceView.vue:90`, `PublicationView.vue:92`, `FilesPanel.vue:121`.

**Change.** `composables/useDebouncedWatch.ts` (cleans up through `onScopeDispose`) and `composables/usePoll.ts`
(`usePoll(tick, delay = pollDelay)`: start, stop, a generation guard, stop on unmount); `useJobPoll` rebuilt on it;
`RunView`, `SourceView` and `PublicationView` moved. `SetupWizard`'s fixed 2 s interval stays. **A failed request
reaches the caller** (review on #960): today `useJobPoll` sets `error` and leaves `job` null, so a caller that reads
only `job` and `polling` shows "running" for ever (#963's `PiiActions`); the poll stops and its state says failed.

**Proof.** The three admin-UI commands; specs with fake timers: nothing fetched after unmount, the backoff steps, stop
when the tick says done, and a failed request ends the poll in a failed state.

### 6. Shared run machinery for `OrganisationSync` and `GatewayModelSync`

**Now.** Two near copies of about 380 lines: the same `Enabled`, the same `rag.admin.sync` counter, the same status
fields, the same config write (advisory lock, apply, save, audit entry, save) and security event, two `RunOutcome`
types, success and failure bookkeeping written two ways. A fix to one (the ETag handling, the error redaction of #232)
has to be copied to the other.

**Change.** `src/Kalla.AdminApi/MirrorSync.cs`: `MirrorSyncState` (status fields, `MarkSucceeded`, `MarkFailed` with
the counter and the redacted error, `Enabled`) and `MirrorSync.WriteAsync` (the lock, the audit entry, the security
event). Each sync keeps its own `ApplyAsync` and log messages. The two keys in
`tests/Kalla.AdminApi.Tests/sql-shapes.json` move to the new method.

**Proof.** `dotnet test tests/Kalla.AdminApi.Tests` (OrganisationSyncTests, GatewayClientTests, SetupEndpointsTests,
SqlSchemaContractTests).

### 7. Shared test doubles in `Kalla.TestKit`

**Now.** A `Counting` handler written twice identically and four more times as variants; five recording handlers;
`ListLogger` four times; a near-identical fake gateway handler in `ChatModelRerankerTests.cs` and
`GatewayRerankerTests.cs`.

**Change.** `CountingHandler`, `RecordingHandler`, `StubHandler` (also an `IHttpClientFactory`) and `ListLogger<T>` in
`tests/Kalla.TestKit/`; the private copies go; `Kalla.Retrieval.Tests` references the TestKit. The Worker test copies
are stream a's and follow in their own pull request.

**Proof.** `dotnet test` on `Kalla.AdminApi.Tests`, `Kalla.Api.Tests` and `Kalla.Retrieval.Tests`, with the same test
count before and after.

## Order and overlap

- **One at a time** (changed 2026-11-23: three backend test runs at once started 44 Testcontainers and the WSL VM ran
  out of memory). Done: #1 (#964), #4 (#965), #2 (#966); then #7, #6, #5, #3.
- **Branches:** the rest use `b/cq-<n>-<slug>` (review on #960): every path is stream b's, and on a `b/` branch
  `scripts/check-ownership.py` checks that. #1, #2, #4 and #7 were already opened on `main-session/` branches.
- **#5 and #3 wait for #963** (B-63, which changes `SourceView` and `CollectionView` too), and #3 goes in one sitting
  at a quiet moment in the queue, since B-58, B-59 and B-66 also add to `AssistantView`, Home and `CollectionView`.
- **Follow-up:** `EvaluationEndpoints.cs` gets #2's paging kit now that #956 has merged (hard-coded `200`, inline
  `LIKE` escaping, its own cursor).
- With the open pull requests: only #2 and #3 touch files of #952–#954, and both leave those lines for a follow-up.
  The UX pass (#955) changes the same views later; these fixes make that work smaller (fewer copies to move).

## Runners-up (not planned yet)

- One advisory-lock helper and one registry of the `0x5241_47xx` lock keys (about ten copies across streams a and b).
- A `RefusalList` component for the three drawers that show refusals the same way.
- `CollectionView.vue` split into one component per tab, best done with the UX pass's UX2.
- `http.del` with a query argument instead of hand-built `?version=` strings.
- `Base64Url` for the conversation cursor (`Kalla.Api/ConversationEndpoints.cs:217`), as `UploadEndpoints` already does.
- A larger call for the user: squashing the 48 EF migrations (about 190,000 lines of designer files) while *no
  backward compatibility before production* holds; it needs an upgrade-guide step.
