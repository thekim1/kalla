#!/bin/sh
# Entrypoint of kalla/renderer (repo-and-deployment §5.4, connectors design §8.3): Playwright's launch-server with
# Chromium and its own sandbox on, nothing else. The WebSocket path is a secret from KALLA_RENDER_WS_PATH: "/" and
# at least 16 characters of [A-Za-z0-9_-]. Without a valid one the container refuses to start.
set -eu
export LC_ALL=C

fail() { echo "kalla/renderer: $*" >&2; exit 64; }

path="${KALLA_RENDER_WS_PATH:-}"
[ -n "$path" ] || fail "KALLA_RENDER_WS_PATH is required (the secret WebSocket path, e.g. /<64 hex characters>)"
case "$path" in
  /*) ;;
  *) fail "KALLA_RENDER_WS_PATH must start with /";;
esac
secret="${path#/}"
[ "${#secret}" -ge 16 ] || fail "KALLA_RENDER_WS_PATH must have at least 16 characters after the /"
case "$secret" in
  *[!A-Za-z0-9_-]*) fail "KALLA_RENDER_WS_PATH may only hold A-Z, a-z, 0-9, _ and - after the /";;
esac

# launch-server takes its options from a file: the image's launch-server.json plus the path, on the tmpfs and readable
# by this user only.
node -e '
  const fs = require("fs");
  const config = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
  config.wsPath = process.env.KALLA_RENDER_WS_PATH;
  fs.writeFileSync(process.argv[2], JSON.stringify(config), { mode: 0o600 });
' /srv/renderer/launch-server.json /tmp/launch-server.json
unset KALLA_RENDER_WS_PATH

echo "kalla/renderer: launch-server (Chromium, sandbox on) on port 3000; the WebSocket path is not logged" >&2
# launch-server prints its endpoint, secret path included, on stdout and nothing else there: discarded. Errors and a
# failed browser launch go to stderr, and the process exits when the browser does.
exec node /srv/renderer/node_modules/playwright-core/cli.js launch-server --browser chromium \
  --config /tmp/launch-server.json >/dev/null
