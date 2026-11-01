# Demo path (2026-11-01, user decision)

**Direction (user, session 16):** reach one complete, working demo of the platform first (crawl, ingest, retrieve, chat with
cited answers in Open WebUI), then dive into details. Don't over-engineer tests or add proofs beyond what a change needs;
merge on green CI. Everything not on the path below waits until the demo runs.

## The demo

A browser session in Open WebUI where a signed-in user asks a question about Umeå kommun and gets an answer with
citations from pages the platform crawled itself, over the real (interim) gateway at `Eu`.

## Path (in order; each step is small)

1. **Stack up** on the local AppHost, `Seed__Enabled=false`, interim gateway (user secrets: `Gateway:BaseUrl`,
   `Gateway:Keys:Eu`, the `EmbeddingProfile:*` values; `docs/p1a-local-run.md` §4.2–§4.3). Started 2026-11-01 on a new empty
   database volume (`DevelopmentVolumes:Postgres` = `ume-rag-pilot-postgres`).
2. **Set-up through the admin API** (OIDC sign-in as the dev `rag-platform-admin`; recreate the small script `adm.py`;
   `HANDOVER.md` session 14 has the order, J7): public data-protection row; department DoIT (local seed); collection
   `Turkos` (slug `turkos`, public, published); crawl source on `/kommunochpolitik` with **`maxPages` ≈ 200** (a slice, not
   all 1,166 pages: minutes, not an hour); umea.se site profile; `Settings.Crawl` (`municipalHosts`, `www.umea.se`
   2 req/s); `PiiOwnDomains`; the Open WebUI client (`internal`) and its key; assistant `turkos` on `claude-opus-5-5`
   (needs the `GatewayModels` row), published, audience = the Open WebUI client.
3. **Crawl once** (sync now); wait for the run; check `/v1/retrieve` with a key scoped to the collection (the right page
   first for two or three questions).
4. **Open WebUI:** put the client id in `OpenWebUi:ClientId` and its whole key in `Parameters:openwebui-api-key` (user
   secrets), restart the AppHost (the `open-webui` container starts), sign in locally, pick `turkos`, ask.
5. **Done when** the cited answer shows in Open WebUI. Record it in `plans/progress.md` (P1a "first cited answer") and
   `HANDOVER.md`.

## After the demo (deliberately deferred)

Gold set, three-way evaluation and thresholds (item 21); EU-route capacity (item 19); P1b item 15 `e2e`, item 17 importer
settings; pin review and `Eu` → `OnPrem` condense default (item 19, before go-live); Aspire 13.6.4; the design sync of
stream B's merged P1b items 9–10; stream B's open fixes (#264, #265, #266); the rest of umea.se; DPIA and the registers.
