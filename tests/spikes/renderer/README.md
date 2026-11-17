# Renderer spike (A-9, to-verify #29)

Throwaway code that proves the risky parts of the renderer design (connectors design §8, repo-and-deployment §5.4)
before it is built, and measures them. Not in `UmeRag.slnx`, not shipped. Results: `plans/work/results-a.md` row 29.

## What runs where

| Container | Networks | What it is |
|---|---|---|
| `renderer` | `render` only (internal, 10.213.0.0/26) | `renderer/Dockerfile`: the official `mcr.microsoft.com/playwright:v1.64.1-noble` image by digest plus `playwright-core` 1.64.1 from `package-lock.json` (the image ships browsers but no Playwright package, so `npx playwright run-server` would download one at every start). `start.sh` runs `run-server` (default; `--unsafe` so the client can turn the sandbox on) or `launch-server` with the sandbox in a server-side config (`RENDER_MODE=launch-server`). User 10002, read-only, tmpfs `/tmp`, `cap_drop: ALL`, `no-new-privileges`, seccomp `seccomp/chrome-sandbox.json` |
| `fixture` | `fixture` only (internal, 10.213.0.64/26) | `fixture/server.py` (stdlib Python) answering as `site.test`, `api.test`, `cdn.test`, `tracker.test`, with `LATENCY_MS` (100) before every answer |
| `spike` (profile `run`) | `render` and `fixture` | the "worker": `RendererSpike` published to `out/` and run on the worker's base image (`aspnet:10.0.15-noble-chiseled-extra`, user 1654, read-only) |
| `lightpanda` (profile `lightpanda`) | `render` only | `lightpanda/browser:1.0.3` by digest, CDP on 9222 |

The renderer can reach only the spike; the spike's fetches go through the worker's real `EgressGuard` and
`CrawlHttpClient` (project reference to `src/Ume.Rag.Worker`, `Crawl:AllowedPrivateRanges` = the fixture subnet);
`RenderFetcher` adds what `HttpFetcher` lacks for a browser (method, request headers and body forwarded, response
headers returned). Spike-only shortcut: `https://*.test` is fetched over plain HTTP, so a page can be a secure context.

`seccomp/chrome-sandbox.json` is moby's default profile (`github.com/moby/profiles`, `seccomp/default.json`, main on
2026-11-07) plus one rule allowing `clone`, `unshare` and `chroot` without `CAP_SYS_ADMIN`, which Chromium's
namespace sandbox needs. The often-cited `jessfraz/dotfiles` `chrome.json` no longer starts a container on Docker 29
(runc fails before the process starts).

## Run

```bash
cd tests/spikes/renderer
dotnet publish RendererSpike -c Release -o out
mkdir -p results && chmod 777 results
docker compose up -d --build renderer fixture
docker compose run --rm spike versions      # 1: version handshake (428 on a minor mismatch)
docker compose run --rm spike checks        # 2, 3, 5, 9, 10, 11, 12: routing, bypasses, readiness, output, scroll, navigation, cookies
docker compose run --rm spike measure 1 2   # 6, 7: the scenarios at concurrency 1 and 2; renderer peak: docker compose exec renderer cat /sys/fs/cgroup/memory.peak
docker compose --profile lightpanda up -d lightpanda && docker compose run --rm spike lightpanda   # 8
docker compose run --rm spike smoke         # one page; used for the sandbox variants below
docker compose --profile lightpanda --profile run down -v
```

Variants (environment for `docker compose up -d --force-recreate renderer`): `RENDER_UNSAFE=false` (run-server drops
the client's `chromiumSandbox`), `RENDER_SECCOMP=builtin` (Docker's default profile), `RENDER_MODE=launch-server`,
`FIXTURE_LATENCY_MS=0`. Inside the renderer, `node -` with a script on stdin shows what Chromium runs as and what the
container can resolve or reach (the root file system is read-only, so scripts go through stdin).

## Scenarios

- **`spa/{n}.html`** (20 pages sharing one bundle): a shell with a classic script, a module bundle (`bundle.js`) with a
  static and a dynamic `import()`, a module reached through a 301 (`old-bundle.js` → `v2/redirected.js`, which imports
  `./rel.js`), a stylesheet with a font and a background image, an image, a tracker script, an iframe, a preflighted
  cross-origin `fetch` (custom header), an XHR, a JSON `POST`, an API call answered with a 302; the text comes from
  `api.test`.
- **`shell.html`**: the same HTML every time, the items from `/api/news`; `GET /__bump` changes them.
- **`static/{n}.html`**: server-rendered baseline.
- **`bypass.html`**: WebSockets (to a site, to the run-server on loopback and by name), `fetch` to loopback, the
  metadata address and a public IP, `sendBeacon`, `dns-prefetch`/`preconnect`/`prefetch`, a service worker, a
  dedicated worker's `fetch`, WebRTC with a STUN server, `EventSource`.
- **`sw.html`** (loaded as `https://site.test/`): service worker registration with `ServiceWorkers` Block and Allow.
- **`nav.html`**, **`refresh.html`**, **`popup.html`**: `location.href` on a timer, `<meta http-equiv=refresh>`,
  `window.open` and an `<a download>` click.
- **`cookie.html`**: `Set-Cookie` on the HTML, a same-origin API that answers 401 without the cookie.
- **`scroll.html`**: an `IntersectionObserver` lazy section below a 3000 px filler and an infinite list (4 pages of 10).
