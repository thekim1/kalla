# Finished queue items up to v0.9.1

The ticked items of the work queues, moved here unchanged when `v0.9.1` was released (2026-11-21).
History: it does not bind. An item is found by its ID (`B-60`); the queues in `plans/work/` hold only open and waiting
items.

## Stream B (`stream-b.md`)

### [x] B-60 The admin shows evaluation questions whose documents were deleted (#893, A-38's admin part)
- **Do:** admin §7.1: a `stale` human question (`State = 'stale'`, `RetiredAt` null) says "Its expected document was
  deleted" in the questions table, with re-point (edit) and retire; the report card (§7.2) shows the run result's
  `documentGone` ("N questions left out: their documents were deleted"). The fields are listed on #893.
- **Paths:** `src/Kalla.AdminApi/**`, `src/admin-ui/**` (not `src/platform/**`), `tests/**` (the names as they are
  after the rename).
- **Done when:** an API test for the stale state and the count; the table and the card shown in a browser.
- **Waits on:** the rename (user, 2026-11-20).
- **Read:** admin §7.1, §7.2; processing §13.3; #893.
