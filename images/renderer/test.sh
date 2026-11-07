#!/usr/bin/env bash
# Image test for ume-rag/renderer (repo-and-deployment §5.4, connectors design §8.3). Runs the image as Compose does
# (user 10002, read-only root file system, tmpfs /tmp, no capabilities, no-new-privileges, images/renderer/seccomp.json,
# an init process) on an internal network of its own, and checks:
#   - it refuses to start without UME_RAG_RENDER_WS_PATH or with a path that breaks the rule ("/" + >= 16 of
#     [A-Za-z0-9_-]), and Chromium refuses to launch under Docker's default seccomp profile (no sandbox, no browser);
#   - the health check turns healthy without the secret; every process runs as uid 10002 on a read-only root;
#   - a client on the network (the image's own playwright-core, as the worker's NuGet) connects with the path, renders a
#     routed page that needs JavaScript, gathers no WebRTC candidate; a wrong path is refused; Chromium's renderers run
#     in a user namespace of their own (the sandbox is on);
#   - from inside the renderer nothing outside its network resolves or connects: no default route, public names and
#     the name of a container on another network don't resolve, and public addresses, the metadata address and that
#     container (which answers on its own network) can't be reached.
#
# Usage: images/renderer/test.sh [image]   (default image: ume-rag/renderer:dev)
# RENDER_TEST_SUBNET [10.214.0.0/24] and RENDER_TEST_OUTSIDE_SUBNET [10.214.1.0/24] choose the test networks' subnets.
set -euo pipefail

image="${1:-ume-rag/renderer:dev}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
seccomp="$here/seccomp.json"
id="ume-rag-rendertest-$$"
net="$id-render"; outside_net="$id-outside"
renderer="$id-renderer"; outside="$id-outside"
subnet="${RENDER_TEST_SUBNET:-10.214.0.0/24}"
outside_subnet="${RENDER_TEST_OUTSIDE_SUBNET:-10.214.1.0/24}"
ws_path="/$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"

cleanup() {
  docker rm -f "$renderer" "$outside" "$id-refuse" >/dev/null 2>&1 || true
  docker network rm "$net" "$outside_net" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  docker ps -a --format '{{.Names}}' | grep -qx "$renderer" && { echo "--- logs of $renderer" >&2; docker logs --tail 40 "$renderer" >&2; }
  exit 1
}
ok() { echo "ok: $*"; }

# hardened NAME [docker run options...] — the Compose hardening of the renderer service.
hardened=(--user 10002:10002 --read-only --tmpfs /tmp:size=512m --cap-drop ALL --security-opt no-new-privileges
  --security-opt "seccomp=$seccomp" --init --memory 2g --cpus 2 --pids-limit 512)

docker network create --internal --subnet "$subnet" "$net" >/dev/null
docker network create --subnet "$outside_subnet" "$outside_net" >/dev/null

# --- Refuses to start without a valid secret path ----------------------------------
refuses() { # refuses EXPECTED-MESSAGE [docker run options...]
  local expected="$1"; shift
  local out code=0
  out="$(docker run --name "$id-refuse" --network "$net" "${hardened[@]}" "$@" "$image" 2>&1)" || code=$?
  docker rm -f "$id-refuse" >/dev/null 2>&1 || true
  [ "$code" != 0 ] || fail "started with $* (exit 0)"
  grep -qF "$expected" <<<"$out" || fail "with $*: expected '$expected', got: $out"
}
refuses "UME_RAG_RENDER_WS_PATH is required"
refuses "must start with /" -e UME_RAG_RENDER_WS_PATH=abcdefghijklmnopqrstuvwxyz
refuses "at least 16 characters" -e UME_RAG_RENDER_WS_PATH=/abc123
refuses "may only hold" -e "UME_RAG_RENDER_WS_PATH=/abcdefghijklmnop/../x"
ok "refuses to start without UME_RAG_RENDER_WS_PATH or with a path that breaks the rule"

# Docker's default seccomp profile blocks the user namespaces Chromium's sandbox needs: the browser must not start.
code=0
out="$(docker run --name "$id-refuse" --network "$net" --user 10002:10002 --read-only --tmpfs /tmp --cap-drop ALL \
  --security-opt no-new-privileges --init -e "UME_RAG_RENDER_WS_PATH=$ws_path" "$image" 2>&1)" || code=$?
docker rm -f "$id-refuse" >/dev/null 2>&1 || true
[ "$code" != 0 ] || fail "launch-server ran under Docker's default seccomp profile"
grep -q "No usable sandbox" <<<"$out" || fail "under Docker's default seccomp profile, expected 'No usable sandbox', got: $out"
grep -qF "${ws_path#/}" <<<"$out" && fail "the secret path appears in the log"
ok "under Docker's default seccomp profile Chromium refuses to run without its sandbox (exit $code)"

# --- Starts hardened and turns healthy --------------------------------------------
docker run -d --name "$renderer" --network "$net" "${hardened[@]}" -e "UME_RAG_RENDER_WS_PATH=$ws_path" "$image" >/dev/null
for _ in $(seq 1 60); do
  [ "$(docker inspect -f '{{.State.Running}}' "$renderer")" = true ] || fail "the renderer exited"
  health="$(docker inspect -f '{{.State.Health.Status}}' "$renderer")"
  [ "$health" = healthy ] && break
  sleep 1
done
[ "$health" = healthy ] || fail "health check: $health"
ok "health check (port 3000 answers, no secret needed): healthy"
docker logs "$renderer" 2>&1 | grep -qF "${ws_path#/}" && fail "the secret path appears in the log"
ok "the secret path is not in the log"

uids="$(docker top "$renderer" -eo pid,uid | awk 'NR > 1 { print $2 }' | sort -u | tr '\n' ' ')"
[ "$uids" = "10002 " ] || fail "processes run as uid(s) $uids, expected 10002 only"
[ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$renderer")" = true ] || fail "root file system is writable"
ok "every process runs as uid 10002 on a read-only root file system"

renderer_ip="$(docker inspect -f "{{(index .NetworkSettings.Networks \"$net\").IPAddress}}" "$renderer")"

# --- A client renders a page through the routed browser ---------------------------
# The worker's side, in short: connect with the secret path, route every request and answer it (here with a fixed page
# that needs its script), close WebSockets, and gather WebRTC candidates with a public STUN server.
client='
const { chromium } = require("/srv/renderer/node_modules/playwright-core");
const endpoint = process.env.ENDPOINT;
(async () => {
  const wrong = await chromium.connect(endpoint.replace(/\/[^/]*$/, "/wrong-path-0123456789"), { timeout: 10000 })
    .then(b => b.close().then(() => "connected"), e => "refused");
  console.log("wrong-path " + wrong);
  const browser = await chromium.connect(endpoint, { timeout: 15000 });
  console.log("version " + browser.version());
  const context = await browser.newContext();
  await context.routeWebSocket("**/*", ws => ws.close({ code: 1008 }));
  const page = await context.newPage();
  await page.route("**/*", route => route.fulfill({ status: 200, contentType: "text/html", body: `<!doctype html>
    <div id="app">Laddar</div><script>
    document.getElementById("app").textContent = "RENDERED-BY-SCRIPT";
    window.rtc = new Promise(resolve => {
      const found = [];
      const pc = new RTCPeerConnection({ iceServers: [{ urls: "stun:1.1.1.1:3478" }] });
      pc.createDataChannel("probe");
      pc.onicecandidate = e => { if (e.candidate) found.push(e.candidate.candidate); else resolve(found); };
      pc.createOffer().then(o => pc.setLocalDescription(o));
      setTimeout(() => resolve(found.concat(["gathering-timeout"])), 8000);
    });
    </script>` }));
  await page.goto("http://site.test/");
  console.log("text " + await page.textContent("#app"));
  console.log("webrtc " + JSON.stringify(await page.evaluate(() => window.rtc)));
  await browser.close();
})().catch(e => { console.log("error " + e.message); process.exit(1); });
'
out="$(docker run --rm -i --network "$net" --user 10002:10002 --read-only --tmpfs /tmp --cap-drop ALL \
  --security-opt no-new-privileges -e HOME=/tmp -e "ENDPOINT=ws://$renderer_ip:3000$ws_path" --entrypoint node \
  "$image" - <<<"$client" 2>&1)" || fail "client: $out"
while IFS= read -r line; do echo "  client: $line"; done <<<"$out"
grep -qx "wrong-path refused" <<<"$out" || fail "a wrong WebSocket path was accepted"
ok "a wrong WebSocket path is refused"
grep -qx "text RENDERED-BY-SCRIPT" <<<"$out" || fail "the page's script did not run"
ok "the page renders with its script ($(grep '^version ' <<<"$out"))"
grep -qx 'webrtc \[\]' <<<"$out" || fail "WebRTC gathered candidates: $(grep '^webrtc ' <<<"$out")"
ok "WebRTC gathers no candidate (--force-webrtc-ip-handling-policy=disable_non_proxied_udp)"

# Chromium's sandbox: the zygotes and renderers run in a user namespace other than the container's.
namespaces="$(docker exec "$renderer" sh -c 'own="$(readlink /proc/self/ns/user)"
  for p in $(pgrep -f -- "--type=zygote"); do [ "$(readlink /proc/$p/ns/user)" = "$own" ] || echo sandboxed; done' | sort -u)"
[ "$namespaces" = sandboxed ] || fail "no zygote runs in a user namespace of its own: Chromium's sandbox is off"
ok "Chromium's zygote runs in a user namespace of its own (sandbox on)"

# --- Nothing outside the render network ------------------------------------------
# A listener on another, non-internal network: reachable there (control), not from the renderer.
docker run -d --name "$outside" --network "$outside_net" --user 10002:10002 --read-only --cap-drop ALL \
  --security-opt no-new-privileges --entrypoint node "$image" \
  -e 'require("http").createServer((q, s) => s.end("outside")).listen(8080)' >/dev/null
outside_ip="$(docker inspect -f "{{(index .NetworkSettings.Networks \"$outside_net\").IPAddress}}" "$outside")"

probe='
const dns = require("dns").promises, net = require("net"), fs = require("fs");
const connect = (host, port) => new Promise(resolve => {
  const s = net.connect({ host, port, timeout: 3000 });
  s.on("connect", () => { s.destroy(); resolve("connected"); });
  s.on("timeout", () => { s.destroy(); resolve("timeout"); });
  s.on("error", e => resolve(e.code));
});
(async () => {
  const routes = fs.readFileSync("/proc/net/route", "utf8").trim().split("\n").slice(1).map(l => l.split(/\s+/));
  console.log("default-route " + (routes.some(r => r[1] === "00000000") ? "yes" : "no"));
  for (const name of process.argv.slice(1).filter(a => a.startsWith("name=")).map(a => a.slice(5))) {
    console.log("resolve " + name + " " + await dns.lookup(name).then(r => "resolved:" + r.address, e => e.code));
  }
  for (const target of process.argv.slice(1).filter(a => a.startsWith("tcp=")).map(a => a.slice(4))) {
    const [host, port] = target.split(":");
    console.log("connect " + target + " " + await connect(host, +port));
  }
})();
'
control="$(docker run --rm -i --network "$outside_net" --read-only --cap-drop ALL --entrypoint node "$image" - \
  "name=$outside" "tcp=$outside_ip:8080" <<<"$probe")"
grep -q "^connect $outside_ip:8080 connected$" <<<"$control" || fail "control: the outside listener is not reachable on its own network: $control"
grep -q "^resolve $outside resolved:" <<<"$control" || fail "control: the outside container's name does not resolve on its network: $control"
ok "control: the outside container answers and resolves on its own network"

out="$(docker exec -i "$renderer" node - name=example.com name=registry.npmjs.org name=host.docker.internal \
  "name=$outside" tcp=1.1.1.1:443 tcp=8.8.8.8:53 tcp=169.254.169.254:80 "tcp=$outside_ip:8080" <<<"$probe")"
while IFS= read -r line; do echo "  renderer: $line"; done <<<"$out"
grep -qx "default-route no" <<<"$out" || fail "the renderer has a default route"
if grep -E '^(resolve .* resolved:|connect .* connected$)' <<<"$out"; then fail "the renderer reached something outside its network"; fi
[ "$(grep -c '^resolve ' <<<"$out")" = 4 ] && [ "$(grep -c '^connect ' <<<"$out")" = 4 ] || fail "probe output incomplete"
ok "from inside the renderer nothing outside its network resolves or connects"
# The bridge's own gateway is on the subnet and answers at the IP level; on a production host the INPUT rule of
# deploy/host/docker-user-firewall.sh rejects the render subnet. Shown, not checked: it depends on this host.
gateway="$(docker network inspect -f '{{(index .IPAM.Config 0).Gateway}}' "$net")"
if [ -n "$gateway" ]; then
  echo "info: the bridge gateway $gateway: $(docker exec -i "$renderer" node - "tcp=$gateway:22" <<<"$probe" | grep '^connect')"
fi

echo "info: image $(docker image inspect -f '{{.Size}}' "$image") bytes; renderer memory $(docker exec "$renderer" cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo '?') bytes peak"
echo PASS
