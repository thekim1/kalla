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

## Result (2026-11-01, session 17)

The demo runs. Set-up through the admin API as in step 2, with these differences: the Open WebUI client has kind
`open-webui` (not `internal`); a separate `internal` client "Pilot retrieval (checks)" holds the retrieval key; the DoIT
department and the `GatewayModels` rows are seeded by SQL in the local database (no Data API key); the site profile is
`contracts/site-profiles/v1/www.umea.se.profile.json` with preset `sitevision`. The crawl took 2 min 20 s: 193 added, 7
`parse.empty`, 2 `pii.flagged` (reported), 0 failed; state `degraded` with `crawl.limit_pages` (the 200-page cap, as
meant). `/v1/retrieve` put the right page first for "När sammanträder kommunfullmäktige?". In Open WebUI, `turkos`
answered with `[1]` markers and `url_citation` annotations to the crawled Kommunfullmäktige page (2.5 s).

**Model:** `turkos` runs on **`gpt-6.1-sol`**. The interim gateway refuses `claude-opus-5-5` and `claude-haiku-5-5` with
400 `provider_rejected` "`temperature` is deprecated for this model", and `ChatService` always sends `temperature`;
rag-api maps that 400 to a bare 502 `upstream_error`. `mistral-large-4` and `glm-5.3` answered 502 at the gateway.

## After the demo (deliberately deferred)

**Found by the demo:** the chat request sends `temperature` to every model (omit it unless the assistant sets one, or
per model), and an unmapped gateway 4xx is logged nowhere useful (the api log showed nothing for the 502); then back to
`claude-opus-5-5`.


Gold set, three-way evaluation and thresholds (item 21); EU-route capacity (item 19); P1b item 15 `e2e`, item 17 importer
settings; pin review and `Eu` → `OnPrem` condense default (item 19, before go-live); Aspire 13.6.4; the design sync of
stream B's merged P1b items 9–10; stream B's open fixes (#264, #265, #266); the rest of umea.se; DPIA and the registers.
