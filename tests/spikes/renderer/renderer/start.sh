#!/bin/sh
# RENDER_MODE=run-server (default): a browser per connection, launch options from the client (`--unsafe` lets the
# client turn the sandbox on). RENDER_MODE=launch-server: one browser with the sandbox set here, shared by connections.
set -eu
cli=/srv/renderer/node_modules/playwright-core/cli.js
path="/${RENDER_TOKEN:?RENDER_TOKEN is required}"
if [ "${RENDER_MODE:-run-server}" = launch-server ]; then
  printf '{"port":3000,"host":"0.0.0.0","wsPath":"%s","chromiumSandbox":%s,"headless":true}' "$path" "${RENDER_SANDBOX:-false}" > /tmp/launch.json
  exec node "$cli" launch-server --browser chromium --config /tmp/launch.json
fi
if [ "${RENDER_UNSAFE:-false}" = true ]; then
  exec node "$cli" run-server --port 3000 --host 0.0.0.0 --path "$path" --unsafe
fi
exec node "$cli" run-server --port 3000 --host 0.0.0.0 --path "$path"
