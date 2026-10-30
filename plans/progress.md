# Progress: phases

Owner: main session. One box per phase step. The detailed steps are in the progress file each line links to. Scope of
every phase: [`README.md`](README.md) phases table. Binding decisions: [`../01-general-idea.md`](../01-general-idea.md)
§10 and [`weakness-review.md`](weakness-review.md). Tick a box when every box in its linked file is ticked or when the
listed done-when holds.

## P0 Foundation ([`p0/README.md`](p0/README.md))

- [x] Stage 0, main session: [`p0/progress-stage-0.md`](p0/progress-stage-0.md)
- [ ] Stream A, platform and the ingestion side: [`p0/progress-a.md`](p0/progress-a.md)
- [ ] Stream B, application and the serving side: [`p0/progress-b.md`](p0/progress-b.md)
- [ ] Sync point after H2: schema checked against [`platform/data-model.md`](platform/data-model.md); `results-a.md`
      and `results-b.md` folded into [`to-verify.md`](to-verify.md)
- [ ] Integration review: "Design deviations" from every P0 PR settled in the design documents
- [ ] `v0.1.0` released and deployed to the test host (stream A items 11–12)

Owned by the user, outside the streams ([`p0/README.md`](p0/README.md) §5,
[`postponed-decisions.md`](postponed-decisions.md)):
- [ ] Test-host access (#16) and backup target + key escrow location (`to-verify.md` #145)
- [ ] Entra package 1 submitted (#17); ticket id recorded
- [ ] Gateway release 1 agreed with the gateway team (#18; ADR 018)
- [ ] Access to the real gateway hardware for the capacity measurement (#22); register M2 decided if triggered
- [ ] DPIA started with the data protection officer (register G1)
- [ ] A named person and a date for A1, G1, W1, W2, N1, U1 and P2 (phase-start rule; P0 ends only when all have both)

## P1a Walking skeleton ([`README.md`](README.md) P1a row, [`weakness-review.md`](weakness-review.md) §1)

Done when a member of the pilot department asks a question in Open WebUI and gets a cited answer from umea.se content.
Detailed progress files are written with the P1 stream briefs.

- [ ] P1 stream briefs, progress files and prompts written (same A/B seam, [`p0/README.md`](p0/README.md) §6)
- [ ] Pilot-start checks: source inventory, JavaScript need, existing umea.se chat (register W1)
- [ ] Parser and ingestion worker: URL list, umea.se crawl (full traversals; hand-checked Sitevision preset), upload
      ([`ingestion-worker/plan.md`](ingestion-worker/plan.md), [`parser/plan.md`](parser/plan.md))
- [ ] Knowledge API: hybrid retrieval with citations, not-found gate on the vector side
      ([`knowledge-api/plan.md`](knowledge-api/plan.md))
- [ ] Chat API and one assistant in Open WebUI ([`chat-api/plan.md`](chat-api/plan.md))
- [ ] Pilot gold set written with the department; three-way run recorded; not-found thresholds calibrated
      ([`weakness-review.md`](weakness-review.md) §2; `to-verify.md` #143, #168, #169)
- [ ] **P1a done:** first cited answer in Open WebUI

## P1b Pilot hardening ([`README.md`](README.md) P1b row)

- [ ] Gateway release 1 shipped; pilot-team per-level keys in use ([`gateway-changes/plan.md`](gateway-changes/plan.md))
- [ ] Preview, advisory evaluation harness, publish with report and keep/roll back
      ([`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md) §12)
- [ ] Freshness rules: age decay, `demote`, completeness by ratio, removal check, 500-URL `lastmod` sample
      ([`weakness-review.md`](weakness-review.md) §3; `to-verify.md` #170, #171)
- [ ] Assistant drafts → publish with history and restore; admin journey pages for sources and the assistant
      ([`admin/plan.md`](admin/plan.md))
- [ ] Feedback (ratings) with Open WebUI import; history mode `none`; banner
      ([`chat-api/plan.md`](chat-api/plan.md), [`weakness-review.md`](weakness-review.md) §4)
- [ ] DPIA (G1) finished; W2 decided; runbook section for public-records requests
- [ ] `perf.yml` (R1) and nightly evaluation
- [ ] E1 and W3 spikes (`to-verify.md` #174, #175)
- [ ] **Go-live gate** met on hand-graded gold-set answers ([`weakness-review.md`](weakness-review.md) §2)
- [ ] Pilot live; gate re-run monthly; **pilot success** decided at the end with the department

## P2 Broaden ([`README.md`](README.md) P2 row)

- [ ] Gateway release 2 (member groups, delegation) before the first second-department or group audience
- [ ] Second department (own Open WebUI instance until W3 and E1 are solved)
- [ ] Items moved from P1 by [`weakness-review.md`](weakness-review.md) §1
- [ ] Stored chat content, when A1 allows it ([`weakness-review.md`](weakness-review.md) §4)
- [ ] SharePoint and further formats; tools and skills ([`tools/plan.md`](tools/plan.md)); workspaces
      ([`workspaces/plan.md`](workspaces/plan.md))
- [ ] First multi-host deployment (Redis, standby D1, object store S2)

## P3 Scale and refine ([`README.md`](README.md) P3 row)

- [ ] Swedish benchmark (register B1) built on the pilot gold set
- [ ] Measured triggers reviewed (OpenSearch O1, visual builder C2, tools beyond `hosted`)
