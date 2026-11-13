# Main session: work queue

The main session owns the design documents, the roadmap and these queues; it reviews and merges stream PRs and
settles their design deviations. Start each session from [`../../HANDOVER.md`](../../HANDOVER.md) and [`main-handbook.md`](main-handbook.md).

## Standing

- Review every stream PR (live in the browser where it touches a page), settle its "Design deviations" in the design,
  merge when ready. Keep [`../roadmap.md`](../roadmap.md) and the stream queues current.
- New work is written as a queue item (goal, paths, done-when, waits on, read) in the owning stream's file.

## Queue

Finished items move to [`../archive/work/`](../archive/work/) at each release; find one by its ID.

### [ ] M-4 The live block
- **Do:** run it in the roadmap's order with the streams and the user; the three-way gold-set run and the hand
  grading use `scripts/grading/` (README there).
- **Done when:** the go-live gate is met, G1 is finished and the pilot is live; the roadmap moves P1b to Done.

### [ ] M-14 Restructure the plan files (user, 2026-11-13; plan review of session 33)
- **Do, in order:** (1) the roadmap as the one place for the plan ahead; (2) finished queue items to
  `archive/work/`; (3) HANDOVER to state only, the rest to `main-handbook.md`; (4) non-binding papers to
  `plans/proposals/`, the goal statement to `plans/goal.md`; (5) per design document: the process sections (summary of
  decisions, milestones, changes to other documents, answered questions, deferred lists) to the archive, each keeping
  its number and heading with a pointer; (6) the V-rule tables of admin and data-model as one heading per rule. Later,
  as each area is next worked on: stable IDs over §-numbers in new citations, triage of `to-verify.md`, the planning
  section of `decisions.md` into the roadmap.
- **Rule:** no section is renumbered (§-numbers are cited about 2,800 times in code); `scripts/check-doc-refs.py` green.
- **Done when:** (1)–(6) merged; the later ones are noted in the handbook.
- **Status (2026-11-13):** (1)–(4) merged in #655 (`01-general-idea.md` stays in the root: code and the citation check
  read it by path); (5)–(6) in #656 (the citation check follows a moved section's "Moved to" line). The later ones are in
  the handbook's *Lessons* (stable IDs) and *Plan files*.
