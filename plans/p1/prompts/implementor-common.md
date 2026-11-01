# Common rules for every stream-A implementor (Ume.Rag)

The main session points each implementor brief at this file (written in session 10).

You are a stream-A implementor in the Ume.Rag repo (.NET 10 services, RAG platform for Umeå kommun). You work ONLY in
your own git worktree (given in your task). The main repo is `/home/crotalus/research/rag` (branch `main`); do not touch it.

## Read first (in your worktree)
1. `AGENTS.md` (working rules, conventions), `README.md` (build/test commands).
2. `01-general-idea.md` §10 Decisions table — skim the 2026-10-31 rows ("Code first, live testing last", "7-day rule in development", "Low CPU during development").
3. `plans/p1/README.md` §1a, §2 (ownership), §3 (binding done-when); `plans/p1b/README.md` §1–§3 if your item is P1b.
4. The design sections named in your task. Precedence: Decisions table > `plans/weakness-review.md` > `plans/simplification-pass.md` > detail design.
5. The existing code you build on (named in your task). Match its style, naming, logging/event conventions and test patterns.

## Hard rules
- **No git writes.** Never run `git commit`, `git push`, `git merge`, `git rebase`, `git checkout -b`, `git stash`, `git reset`, `git worktree`, `gh pr …`. Read-only git (`status`, `diff`, `log`, `show`) is fine. The main session commits.
- **Ownership:** only change paths owned by stream `a` (or `shared` only if your task says so) in `.github/stream-ownership.txt`. `src/Ume.Rag.Retrieval`, `src/Ume.Rag.Answering`, `src/Ume.Rag.Domain` (except its ports), `src/Ume.Rag.Infrastructure/Persistence/**`, `tests/Ume.Rag.IntegrationTests`, the AdminApi/Api and the AppHost are stream B's: use them, don't edit them. If you need a schema/column/B-side change, don't make it: build against the agreed contract, and write the request in your report (I file the issue). Design documents (`plans/**`, root `*.md`) are read-only: list deviations in your report.
- Database tests go in `tests/Ume.Rag.Worker.Tests` or `tests/Ume.Rag.Pipelines.Tests` on `TestPostgres` (see existing `Engine/*Tests.cs`).
- Everything on FakeGateway / fixtures / Testcontainers. No live network calls (no umea.se, no real gateway).
- Code, comments, docs in English. Keep comments short and factual.
- Respect the "Deferred or removed" sections of each design: don't build P2 items.
- Package pins: avoid new packages; if unavoidable, exact version via `Directory.Packages.props` (shared — tell me instead of editing).
- **No backward compatibility before production** (Decisions table, 2026-11-01): change schemas, contracts, payloads and APIs in one step; don't add fallbacks for old rows/payloads or compatibility shims (remove existing ones you touch). Development data is rebuilt or re-crawled.
- If an approach fails 3 times, stop and switch strategy; if blocked, stop and report rather than inventing scope.

## Proofs you must deliver
- Tests that prove the binding done-when of your item(s), named in your report.
- **A negative proof**: for the central behaviour, show the test fails when the behaviour is broken (temporarily break the code or remove the guard, run the test, see it fail with the expected message, restore). Report the exact break and the failing output line.
- **GitHub Actions are off**: never trigger CI (no `ci` label, no dispatch). Everything is verified locally.
- **Test hygiene:** page and chunk texts unique per test (include a GUID): the database and embedding cache are shared across tests. `await using` every worker host; a leaked host fails every later test in `RagDatabaseFixture.ResetAsync`. Queries that tests compare must have an `ORDER BY`. Tests over "all sources" pause the other sources first (`EngineKit.PauseOtherSourcesAsync`).
- Build and suites, **always under `taskset -c 0,1`** (CI has 2 vCPUs; other agents share the machine):
  ```
  taskset -c 0,1 dotnet build UmeRag.slnx -c Release --nologo        # 0 warnings, 0 errors (warnings are errors)
  UME_RAG_TEST_CLAMAV_IMAGE=ume-rag.local/clamav:dev UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci \
    taskset -c 0,1 dotnet test --project tests/Ume.Rag.Worker.Tests -c Release --no-build
  taskset -c 0,1 dotnet test --project tests/Ume.Rag.Pipelines.Tests -c Release --no-build
  taskset -c 0,1 dotnet test --project tests/Ume.Rag.ContractTests -c Release --no-build   # if you touch contracts/ or Ume.Rag.Pipelines
  ```
  Run your new test classes 3 times to check for flakiness. Report pass/fail counts verbatim.
- If you change `Ume.Rag.Pipelines` job records, regenerate the `contracts/jobs/v1/` schemas the way the drift test says (see `tests/Ume.Rag.ContractTests`), and keep the drift test green.
- Tick the matching boxes in the stream-A progress file named in your task (`[x] … (PR: <your branch>)`) and add rows to the results file if your task names to-verify rows (same table shape as the existing rows; date 2026-10-31).

## Report (your final message)
1. Summary of what you built (files, main types).
2. Done-when evidence: test names + verbatim pass counts of every suite above.
3. The negative proof (break, failing line, restored).
4. **Design deviations** (numbered, each the smallest sound option).
5. Requests to stream B (schema/API needs), if any, with the data-model / design section.
6. Anything left open, and why.
7. A ready PR body following `.github/pull_request_template.md` (sections: Item, Done when, Design deviations, To-verify results, Hand-offs). Use "Stream a, P1a item N (`plans/p1/README.md` §3)" or "P1b item N (`plans/p1b/README.md` §3)" in the Item section.
