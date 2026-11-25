# CI runner host

The self-hosted runners `ume-rag` and `ume-rag2` share one machine and one user (Decisions "CI on the self-hosted
runner for every pull request"). This page lists what that machine needs on a fresh **Ubuntu 26.04**. The workflows
install .NET (`actions/setup-dotnet`), Node (`actions/setup-node`), the Aspire CLI, PowerShell (as a .NET tool) and the
Playwright browser themselves, so they are not installed by hand.

The browser tests (`e2e.yml`) run by hand from the Actions tab, or from `release.yml` when asked: set the repository
variable `RELEASE_E2E` to `true` for tag releases, or tick `e2e` on a dry run (Decisions "E2E optional on release").

Run them **alone**: dispatch `e2e.yml` only when no other workflow is running (`gh run list --status in_progress`).
With `verify.yml`'s backend and renderer jobs on the other runner at the same time, the admin UI's requests failed in
the browser ("The server cannot be reached") although the same commit passed alone. `release.yml` starts them after
`verify`, `parser` and `build` for that reason (`build` runs beside `verify` and `parser`).

## Working with the workflows

- **Before a release, run a release dry run** and fix what it finds:
  `gh workflow run release.yml -f version=X.Y.Z-dryrun -f e2e=false` (about 14 minutes; a tag run about 17). It is the only way to run the
  bundle's smoke test (`deploy/tests/release-smoke.sh`) and the Portainer stack's boot test
  (`deploy/tests/portainer-boot.sh`: the release's `compose.portainer.yaml` with the gateway's pinned release, `clamav`,
  `openwebui` and `proxy`) before the tag, and it never reaches the publish job. `-f portainer_boot=X.Y.Z` runs only
  the boot test, against that version's published images.
- **Actions are pinned by commit SHA** (`uses: owner/name@<40-hex commit> # vX.Y.Z`), and `verify.yml`'s `doc-refs`
  job fails on a tag. To move one: take the commit of the release's tag
  (`gh api repos/<owner>/<name>/commits/<tag> --jq .sha`), check it is the release you mean, and change the SHA and
  the comment in every workflow that uses it.
- **The runner has no sudo, no .NET 8 and no `gh`.** A workflow that needs a tool downloads a pinned, checksummed copy
  (as `release.yml` does for Syft and `gh`) or rolls forward (`DOTNET_ROLL_FORWARD: Major` for the net8-only SBOM tool).
- **Reading a failed smoke test:** `gh run view <id> --log-failed` is long; grep `ok:|error:` for the script's own
  lines. An `up --wait` "not healthy within 900s" a few seconds after the start means a one-shot container failed:
  read its log lines.
- **A flaky test can be a real bug** (#471): read the failed attempt's full log (`gh run view <id> --attempt 1
  --log-failed`) before calling it a race.
- **`/tmp` on the runner host is a tmpfs** (half the RAM, about 5.5 GiB), not disk. A tool that unpacks images or
  large archives there fills it and takes the RAM with it: the 0.11.0 tag run's Syft scan left 2.9 GiB behind, and the
  next attempts failed with *no space left on device* and out-of-memory kills of test containers (#1069).
  `release.yml` points Syft's `TMPDIR` at `$RUNNER_TEMP` and stops a leftover scan at the start and the end of the
  job; give any new tool of that kind a `TMPDIR` under `$RUNNER_TEMP` too. If a release job dies hard, check the host
  for `syft scan` processes and `stereoscope-*` directories (`/usr/local/sbin/runner-cleanup.sh` prunes old ones).
- `gh pr checks --watch` right after a push returns at once with "no checks reported": wait about 30 s first.
- **Fixed test subnets:** the renderer image test uses `10.214.0.0/24` and `10.214.1.0/24`, the worker's renderer
  container test in `verify.yml` `10.215.0.0/24`. Both runners share one Docker daemon, so two renderer jobs at once can
  collide.

## Packages (once, with sudo)

```bash
sudo apt-get update
# Runner and .NET prerequisites
sudo apt-get install -y git curl ca-certificates tar unzip libicu-dev libssl-dev libkrb5-3 zlib1g
# Docker Engine; the runner user must reach the daemon without sudo
sudo apt-get install -y docker.io docker-compose-v2 docker-buildx
sudo usermod -aG docker "$RUNNER_USER"        # the user the runner services run as; restart the services afterwards
```

Docker must use cgroup v2 with the systemd driver, as on the test host:
`docker info --format 'driver={{.CgroupDriver}} version={{.CgroupVersion}}'` prints `driver=systemd version=2`.

## Chromium's system libraries (for `e2e.yml`)

The runner has no sudo, so the workflow only downloads the browser; its shared libraries come from the host. From a
checkout of the repository, with the Playwright version pinned in `tests/e2e/package.json`:

```bash
cd tests/e2e
npm ci
sudo npx playwright install-deps chromium
```

If `install-deps` does not know Ubuntu 26.04 and refuses, install the libraries directly (the Ubuntu 24.04 list
Playwright uses for Chromium; the `t64` names carry over):

```bash
sudo apt-get install -y libasound2t64 libatk-bridge2.0-0t64 libatk1.0-0t64 libatspi2.0-0t64 libcairo2 libcups2t64 \
  libdbus-1-3 libdrm2 libgbm1 libglib2.0-0t64 libnspr4 libnss3 libpango-1.0-0 libx11-6 libxcb1 libxcomposite1 \
  libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 fonts-liberation fonts-noto-color-emoji
```

## Check

As the runner user, after one `e2e.yml` run (dispatch it from the Actions tab) (or `npx playwright install chromium` in `tests/e2e`):

```bash
ldd ~/.cache/ms-playwright/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell | grep 'not found'
```

prints nothing. A library it still names is the package to add (`apt-file search <lib>` finds it). A missing library shows
in CI as `browserType.launch: Target page, context or browser has been closed` after 0 ms (#370).
