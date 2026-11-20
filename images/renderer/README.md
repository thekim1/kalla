# kalla/renderer

Chromium behind Playwright's `launch-server`, nothing else: no crawling logic, no volumes, no other process. The worker
drives it over WebSocket with Playwright .NET and fetches every request the page makes itself, through its guarded
client (connectors design §8.3); the container sits only on the internal `render` network (repo-and-deployment §5.4,
§6.1). Checked in the A-9 spike (`tests/spikes/renderer/`, results-a row 29).

## Contract

What the worker, the Compose file, the AppHost and the worker's integration tests rely on. A change here is a change
for all of them.

| Item | Value |
|---|---|
| Base | `mcr.microsoft.com/playwright:v1.64.1-noble@sha256:2f05f8a734509a7b3f26a912639488ee08f58a7b292d14d62271bcff9038768f` |
| Playwright | `playwright-core` **1.64.1** from `package-lock.json` (`npm ci` at build time; nothing is downloaded at start). The worker's `Microsoft.Playwright` NuGet is the same exact version (the server itself checks only major.minor) |
| Process | `node …/playwright-core/cli.js launch-server --browser chromium --config <file>` (`start.sh`), one shared headless Chromium (the headless shell) |
| Config | `launch-server.json` plus the path: `{ headless: true, chromiumSandbox: true, port: 3000, host: "0.0.0.0", wsPath: <path>, args: ["--force-webrtc-ip-handling-policy=disable_non_proxied_udp"] }` |
| Secret path | environment variable **`KALLA_RENDER_WS_PATH`**, required: `/` followed by at least 16 characters of `[A-Za-z0-9_-]` (production: `/` + 64 hex characters, `init-deployment.sh`; the AppHost: 32 alphanumerics). Missing or invalid: the container exits with code 64 and says why. The path is never logged (launch-server prints its endpoint on stdout, which is discarded) |
| Endpoint | `ws://<host>:3000<path>`; a wrong path gets HTTP 400 |
| WebRTC | off: `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` (no proxy exists, so no ICE candidate is gathered and no mDNS or IGMP packet leaves; `--webrtc-ip-handling-policy` alone has no effect, measured) |
| User | `10002:10002` (no passwd entry needed), `HOME=/tmp` |
| Runs with | `read_only: true`, a `tmpfs` on `/tmp` (512m in Compose: Chromium's profile and shared memory, Playwright passes `--disable-dev-shm-usage`), `cap_drop: [ALL]`, `no-new-privileges`, **`seccomp=images/renderer/seccomp.json`**, `init: true` (reaps Chromium's children) |
| Seccomp | `seccomp.json`: Moby's default profile plus one rule allowing `clone`, `unshare` and `chroot` without `CAP_SYS_ADMIN`, which Chromium's namespace sandbox needs. Under Docker's default profile Chromium exits with "No usable sandbox" and so does the container: it fails closed, never without the sandbox |
| Health | the image's `HEALTHCHECK`: TCP connect to 127.0.0.1:3000, no secret needed. launch-server closes the port and exits when Chromium exits, so a dead browser is an exited container (Docker restarts it) |
| Network | in production only `render` (internal, 172.30.18.0/24), shared with `kalla-worker`; the host firewall rejects that subnet at the host (`INPUT`) |

## Image

The Dockerfile installs `playwright-core`, upgrades the base's Ubuntu packages (`apt-get upgrade`: each build takes the
security fixes released since the base was built), removes npm and yarn (only `node` runs), removes Firefox, WebKit and
full Chromium (only the headless shell is launched), makes `/ms-playwright` read-only for everyone but root (the base
leaves it world-writable), and copies the result into one layer `FROM scratch`: deleting in a `RUN` step alone would
keep the browsers in the base's layers. About 1.46 GB against the base's 2.54 GB. The base's environment (`PATH`, `LANG`, `LC_ALL`, `PLAYWRIGHT_BROWSERS_PATH`) is set
again in the final stage.

```bash
docker build -t kalla/renderer:dev images/renderer
bash images/renderer/test.sh kalla/renderer:dev
docker run --rm --network <internal network> --user 10002:10002 --read-only --tmpfs /tmp:size=512m --cap-drop ALL \
  --security-opt no-new-privileges --security-opt seccomp=images/renderer/seccomp.json --init \
  -e KALLA_RENDER_WS_PATH=/<at least 16 of A-Za-z0-9_-> kalla/renderer:dev
```

`test.sh` runs it as Compose does on internal networks of its own (`RENDER_TEST_SUBNET`, default 10.214.0.0/24, and
`RENDER_TEST_OUTSIDE_SUBNET`, 10.214.1.0/24) and checks: it refuses to start without a valid path; Chromium refuses to
run under Docker's default seccomp profile; it turns healthy; every process is uid 10002 on a read-only root; a client
with the image's own `playwright-core` renders a routed page that needs its script, gathers no WebRTC candidate, and is
refused on a wrong path; Chromium's zygote runs in a user namespace of its own (the sandbox is on); and from inside the
renderer there is no default route, public names and another network's container name don't resolve, and public
addresses, `169.254.169.254` and that container (which answers on its own network) can't be reached.

## Updating Playwright

Move the base image (by digest), `package.json` and `package-lock.json` (`npm install --package-lock-only
playwright-core@X.Y.Z`, then check the `integrity`) and the worker's `Microsoft.Playwright` NuGet together, to the
same exact version, then run `test.sh`. A new Chromium may need the seccomp profile refreshed from Moby's default
(`github.com/moby/profiles`, `seccomp/default.json`) with the same added rule.
