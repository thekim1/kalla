# Main session: work queue

The main session owns the design documents, the roadmap and these queues; it reviews and merges stream PRs and
settles their design deviations. Start each session from [`../../HANDOVER.md`](../../HANDOVER.md).

## Standing

- Review every stream PR (live in the browser where it touches a page), settle its "Design deviations" in the design,
  merge when ready. Keep [`../roadmap.md`](../roadmap.md) and the stream queues current.
- New work is written as a queue item (goal, paths, done-when, waits on, read) in the owning stream's file.

## Queue

### [x] M-1 Run the browser tests locally (covered by the runner's green run of #377: https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Do:** `scripts/Start-E2E.ps1` (stops the dev stack; own test database), then `npx playwright test` in
  `tests/e2e`.
- **Done when:** the pilot journey (admin §5.4 steps 1–13, mouse and keyboard) and the accessibility spec pass
  locally, or their failures are fixed or filed. This is the local evidence for the old items 11, 12 and 14; B-5 and
  A-2 still need the runner.

### [x] M-2 Recommendations for the open design calls
- **Do:** one short proposal each, with a default: topics for uploads; `UrlCanonicalizer` into Pipelines (#178);
  #217's fired-types list; identity §16 Q2–4, Q8; a direct connection for the HNSW rebuild behind a pooler; index
  health for multi-collection scopes; recall for a narrow topic in a large collection.
- **Done when:** the user has answered; the answers are in the design or the Decisions table.

### [ ] M-3 Code complete
- **Do:** when every code item of both queues is ticked: settle every remaining design deviation, fold the results
  files into `to-verify.md`, run the full CI set once.
- **Done when:** nothing open in the code sections of the queues; the roadmap says P1b code done.

### [ ] M-4 The live block
- **Do:** run it in the roadmap's order with the streams and the user; the three-way gold-set run and the hand
  grading use `scripts/grading/` (README there).
- **Done when:** the go-live gate is met, G1 is finished and the pilot is live; the roadmap moves P1b to Done.
