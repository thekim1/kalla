# Progress: phases

Owner: main session. One box per phase step. The detailed steps are in the progress file each line links to. Scope of
every phase: [`README.md`](README.md) phases table. Binding decisions: [`../01-general-idea.md`](../01-general-idea.md)
§10 and [`weakness-review.md`](weakness-review.md). Tick a box when every box in its linked file is ticked or when the
listed done-when holds.

## P0 Foundation ([`p0/README.md`](p0/README.md))

- [x] Stage 0, main session: [`p0/progress-stage-0.md`](p0/progress-stage-0.md)
- [x] Stream A, platform and the ingestion side: [`p0/progress-a.md`](p0/progress-a.md) (done 2026-10-31; open
      boxes moved to P1b by decision: test-host drill and deploy, H8 capacity run)
- [x] Stream B, application and the serving side: [`p0/progress-b.md`](p0/progress-b.md) (done 2026-10-31; its
      open boxes are P1/P2 work by design: pool waits with R1, #151 with Entra, `cancel-run` with `Runs`)
- [x] Sync point after H2 (2026-10-30, PRs #5, #7, #8, #9; repeated at integration for later results): schema checked against [`platform/data-model.md`](platform/data-model.md); `results-a.md`
      and `results-b.md` folded into [`to-verify.md`](to-verify.md)
- [x] Integration review: "Design deviations" from every P0 PR settled in the design documents
      (stream A's merged PRs 2026-10-30, session 8; stream B's 2026-10-31, session 9; Decisions table rows "P0 integration")
- [x] `v0.1.0` released 2026-10-31 (stream A item 11, https://github.com/thekim1/ume-rag-platform/releases/tag/v0.1.0); the test-host deploy (item 12) moved to P1b (Decisions table, "M2 and test hosting")

Owned by the user, outside the streams ([`p0/README.md`](p0/README.md) §5,
[`postponed-decisions.md`](postponed-decisions.md)):
- [ ] Test-host access (#16) and backup target + key escrow location (`to-verify.md` #145)
- [ ] ~~Entra package 1 submitted (#17)~~: moved to P2; the pilot signs in with Keycloak (Decisions table, P1 pilot sign-in)
- [ ] Gateway release 1 agreed with the gateway team (#18; ADR 018)
- [ ] Access to the real gateway hardware for the capacity measurement (#22); register M2 decided if triggered
- [ ] DPIA started with the data protection officer (register G1)
- [ ] A named person and a date for A1, G1, W1, W2, N1, U1 and P2 (phase-start rule; P0 ends only when all have both)

## P1a Walking skeleton ([`p1/README.md`](p1/README.md), [`README.md`](README.md) P1a row, [`weakness-review.md`](weakness-review.md) §1)

Done when a member of the pilot department asks a question in Open WebUI and gets a cited answer from umea.se content.
Checklist and hand-offs: [`p1/README.md`](p1/README.md) §3–§4. Every model call is EU-hosted until on-prem hardware
exists (M2 triggered, user 2026-10-30; `p1/README.md` §1).

- [x] P1 stream briefs, progress files and prompts written (same A/B seam, [`p0/README.md`](p0/README.md) §6):
      [`p1/stream-a.md`](p1/stream-a.md), [`p1/stream-b.md`](p1/stream-b.md), [`p1/prompts/`](p1/prompts/)
- [x] J1: `v0.1.0` tagged; P1 ownership rules (`p1/README.md` §2) merged (2026-10-31, #57)
- [ ] Stream A, the worker side: [`p1/progress-a.md`](p1/progress-a.md)
- [ ] Stream B, the serving side: [`p1/progress-b.md`](p1/progress-b.md)
- [ ] Pilot-start checks: source inventory, JavaScript need (`to-verify.md` #139); W1 answered (Team Turkos's
      `chat.umea.se`; the product owner decides the relation)
- [x] Sync point after J2/J3: schema and contracts checked; results folded into [`to-verify.md`](to-verify.md) (2026-10-31, `4b0f3a6`; repeated for later results)
- [ ] Local skeleton: fixture pages answered with a citation in Open WebUI on FakeGateway (`p1/README.md` §5)
- [x] **Milestone 1** (first live run, 2026-11-01, session 13): five public umea.se pages ingested through the interim
      gateway (`text-embedding-3-large`, 1024 dims via `dimensions`, `Eu`), canary recorded, `/v1/retrieve` answered with
      the right chunk first; run steps and findings in [`../docs/p1a-local-run.md`](../docs/p1a-local-run.md) §4 (#168 for
      the AppHost dev-run bugs; Decisions row "Personal data on the municipality's own published site")
- [ ] Gateway session: gateway reachable with EU routes and the pilot team's keys (J10); shared Keycloak with RAG's
      realm additions (J11)
- [ ] Pilot gold set written with the department; three-way run recorded; not-found thresholds calibrated
      ([`weakness-review.md`](weakness-review.md) §2; `to-verify.md` #143, #168, #169)
- [x] **Demo first** (user, 2026-11-01): crawl a slice of umea.se, retrieve, and a cited answer in Open WebUI, by the five steps of
      [`demo-path.md`](demo-path.md); the gold set, evaluation, capacity and the other details follow the demo. **Done
      2026-11-01 (session 17):** 200-page slice of `/kommunochpolitik` (193 added, 0 failed, 2 min 20 s), `/v1/retrieve`
      right page first, `turkos` answered in Open WebUI with `url_citation` annotations to the crawled page, over the
      interim gateway at `Eu` on `gpt-6.1-sol` (`claude-opus-5-5` refuses `temperature`: see `demo-path.md`)
- [ ] **Admin UI focus** (user, 2026-11-01): the demo's set-up from the browser first, then the rest of P1b items 11–13
      ([`demo-path.md`](demo-path.md) "Next focus: the admin UI"; stream B)
- [ ] **P1a done:** first cited answer in Open WebUI, shown locally with the pilot member present

Owned by the user, outside the streams ([`p1/README.md`](p1/README.md) §7):
- [x] P1a shown locally (U-a, decided 2026-10-30)
- [x] The EU embedding model (U-b): `text-embedding-3-large` at 1024 dims, `Eu` (Decisions table, 2026-10-31; proven in milestone 1)
- [ ] Pilot accounts (U-c)
- [ ] Gateway production session briefed and run (U-d); U1 with the web editors (U-e); gold set owner and date (U-f)

## P1b Pilot hardening ([`README.md`](README.md) P1b row)

Briefs, checklist (code and live items) and progress files: [`p1b/`](p1b/README.md) (written early, 2026-10-31).

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
