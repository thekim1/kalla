# Browser tests

Playwright tests of the admin UI (admin design §10.1, §11.4; repo-and-deployment §8.2 `e2e`), copied from the gateway's
`tests/e2e` setup.

- `specs/pilot.spec.ts`: the pilot department admin's journey (admin design §5.4 steps 1–13) against the fixture website,
  with the mouse and with the keyboard only. It ends by finding the assistant in rag-api's `/v1/models` for the seeded
  Open WebUI (dev) client.
- `specs/accessibility.spec.ts`: axe (WCAG 2.2 A/AA) and target size on every page in the three themes, reflow at 320 px
  and 200 % text, keyboard landmarks, skip link and focus, forced colours. The result is summarised in
  `docs/accessibility-statement.md`.

## Running them

```bash
pwsh scripts/Start-E2E.ps1          # the test stack: own database volume, FakeGateway, fixture website on port 7416
cd tests/e2e
npm ci
npx playwright install chromium
npx playwright test
pwsh scripts/Start-E2E.ps1 -Stop
```

The stack uses the dev stack's ports, so stop the dev stack first. `E2E:Enabled` (set by the script) starts the fixture
website, lets the worker reach `127.0.0.1` and ignores user secrets that point at real systems. The specs read the
development password and the dev client key from the AppHost's user secrets, or from `E2E_PASSWORD` and `E2E_DEV_KEY`.

## The fixture website

`fixture-site/` is served by nginx. The pilot journey crawls `/webbplats/` and keeps the crawl inside it, because the
saved pages link to umea.se paths the fixture site does not have.

| File | Source |
|---|---|
| `webbplats/index.html`, `robots.txt`, `sitemap.xml` | written for these tests |
| `webbplats/sidor/*.html` | copies of `tests/fixtures/umea/pages/` (saved umea.se pages, see its `MANIFEST.csv`) |
| `webbplats/dokument/ks-2026-10-19-foredragningslista.pdf` | copy of `tests/fixtures/umea/pages/` |
| `webbplats/dokument/text-layer.pdf`, `attachment.pdf` | copies of `tests/fixtures/parser/` (synthetic, CC0-1.0) |
