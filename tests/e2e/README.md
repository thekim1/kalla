# Browser tests

Playwright tests of the admin UI (admin design §10.1, §11.4; repo-and-deployment §8.2 `e2e`), copied from the gateway's
`tests/e2e` setup.

- `specs/pilot.spec.ts`: the pilot department admin's journey (admin design §5.4 steps 1–13) against the fixture website,
  with the mouse and with the keyboard only. Step 13 follows the assistant page's "Open in Open WebUI (dev)" link (the
  seeded client's `PortalUrl`, the stack's Open WebUI) and finds the assistant in kalla-api's `/v1/models` for that client.
- `specs/accessibility.spec.ts`: axe (WCAG 2.2 A/AA) and target size on every page in the three themes, reflow at 320 px
  and 200 % text, keyboard landmarks, skip link and focus, forced colours. The result is summarised in
  `docs/accessibility-statement.md`.
- `specs/setup.spec.ts`: the setup wizard (admin design §5.11, identity design §19) on a stack of its own: the code, the
  addresses, failed and passed test sign-ins in the provider's window, Open WebUI's step, the summary, Finish, the first
  sign-in, the stored values and Home's reminder of the secret's expiry, with axe on every step. Finishing closes setup, so
  each run needs a fresh setup stack; on any other stack the spec skips.
- `specs/openwebui.spec.ts`: the stack's Open WebUI against kalla-api (to-verify 47, 50, 134, 165), with an assistant the
  pilot journey published and a local Open WebUI account it signs up: the description in the model selector, citation
  chips and their links, Open WebUI's message ids on every completion and regeneration, no task request reaching a model,
  and Stop ending kalla-api's turn as cancelled. It reads kalla-api's record in the AppHost's Postgres container
  (`docker exec`), and Stop needs a slow answer: export `FakeGateway__Latency__TokenIntervalMs=80` and
  `FakeGateway__Latency__AnswerWords=150` before starting the stack. If the store's first account was made since Open
  WebUI last started (other than by the spec), restart Open WebUI first: making it switched sign-up off until a restart.
- `specs/upload.spec.ts`: uploads into a knowledge base of files it creates (admin design §5.5.7): a file with a known
  name replaces it by default and keeps its slot and document, *Keep both* adds a new file, and the same name in a folder
  is another file.

## Running them

```bash
pwsh scripts/Start-E2E.ps1          # the test stack: own database volume, FakeGateway, fixture website on port 7416
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test --project=pilot --project=accessibility
npx playwright test --project=openwebui --no-deps   # after the pilot project on the same stack
npx playwright test --project=upload                # on the same stack, needs nothing before it
pwsh scripts/Start-E2E.ps1 -Setup   # the setup wizard's stack: Setup:Try on a fresh volume of its own
npx playwright test --project=setup
pwsh scripts/Start-E2E.ps1 -Stop    # stops either stack and removes both test volumes
```

The stack uses the dev stack's ports, so stop the dev stack first. `E2E:Enabled` (set by the script) starts the fixture
website, lets the worker reach `127.0.0.1` and ignores user secrets that point at real systems. The specs read the
development password and the dev client key from the AppHost's user secrets, or from `E2E_PASSWORD` and `E2E_DEV_KEY`.
The setup spec also reads the client secrets `oidc-client-secret` and `openwebui-client-secret` there (or
`E2E_OIDC_CLIENT_SECRET`, `E2E_OPENWEBUI_CLIENT_SECRET`), and the setup code from `aspire logs adminapi` (or
`E2E_SETUP_CODE`; `ASPIRE_CLI` names the CLI when `aspire` is not on the path). It types them without a locator `fill`,
puts none in an assertion message, and its project records no trace, screenshot, video or page snapshot.

## The fixture website

`fixture-site/` is served by nginx. The pilot journey crawls `/webbplats/` and keeps the crawl inside it, because the
saved pages link to umea.se paths the fixture site does not have.

| File | Source |
|---|---|
| `webbplats/index.html`, `robots.txt`, `sitemap.xml` | written for these tests |
| `webbplats/sidor/*.html` | copies of `tests/fixtures/umea/pages/` (saved umea.se pages, see its `MANIFEST.csv`) |
| `webbplats/dokument/ks-2026-10-19-foredragningslista.pdf` | copy of `tests/fixtures/umea/pages/` |
| `webbplats/dokument/text-layer.pdf`, `attachment.pdf` | copies of `tests/fixtures/parser/` (synthetic, CC0-1.0) |
