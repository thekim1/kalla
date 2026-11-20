#!/usr/bin/env bash
# Image test for ume-rag/clamav (repo-and-deployment §5.5). Runs the image hardened as in Compose (its own non-root
# user, read-only root file system, tmpfs /tmp, no capabilities) and talks to clamd over TCP 3310 the way the worker
# will (connectors design §11): zPING, zVERSION, an EICAR zINSTREAM is FOUND, a clean 50 MB stream is OK, a stream over
# StreamMaxLength (60M) is refused, and the image's health check turns healthy.
#
# Signatures:
#   (default)  a one-line test signature for EICAR in a fresh volume, with clamd started directly (no freshclam), so the
#              test needs no network and does not load database.clamav.net (CI).
#   --online   the image's entrypoint on an empty volume: freshclam downloads the real signatures (about 115 MB) from
#              database.clamav.net. Then a second container on the same volume must reuse them without a download.
#
# Usage: images/clamav/test.sh [--online] [image]   (default image: ume-rag/clamav:dev)
set -euo pipefail

online=false
if [ "${1:-}" = "--online" ]; then online=true; shift; fi
image="${1:-ume-rag/clamav:dev}"
id="ume-rag-clamtest-$$"
volume="$id-db"
eicar='X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*'

cleanup() {
  docker rm -f "$id" >/dev/null 2>&1 || true
  docker volume rm "$volume" >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  docker ps -a --format '{{.Names}}' | grep -qx "$id" && { echo "--- logs of $id" >&2; docker logs --tail 40 "$id" >&2; }
  exit 1
}
ok() { echo "ok: $*"; }

# Starts the container hardened as in Compose, publishing clamd on a free loopback port. Arguments before `--` go to
# `docker run` (e.g. --entrypoint), arguments after it to the container.
start() {
  local before=() after=()
  while [ $# -gt 0 ] && [ "$1" != "--" ]; do before+=("$1"); shift; done
  [ $# -gt 0 ] && shift
  after=("$@")
  docker rm -f "$id" >/dev/null 2>&1 || true
  docker run -d --name "$id" --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
    -p 127.0.0.1::3310 -v "$volume:/var/lib/clamav" "${before[@]}" "$image" "${after[@]}" >/dev/null
}

# Waits until clamd answers zPING over TCP (up to 15 minutes: a first download on a slow line).
wait_clamd() {
  for _ in $(seq 1 900); do
    [ "$(docker inspect -f '{{.State.Running}}' "$id" 2>/dev/null)" = true ] || fail "container exited before clamd was up"
    [ "$(clamd_tcp PING 2>/dev/null)" = PONG ] && return 0
    sleep 1
  done
  fail "clamd not answering after 15 minutes"
}

port() { docker port "$id" 3310/tcp | head -n1 | sed 's/.*://'; }

# A small clamd client over TCP, the protocol the worker uses (connectors design §11). Commands use the "z" form
# (NUL-terminated); the reply is printed without its NUL.
#   clamd_tcp PING | VERSION               one command
#   clamd_tcp INSTREAM text <string>       zINSTREAM of the string
#   clamd_tcp INSTREAM zeros <bytes>       zINSTREAM of that many zero bytes
# INSTREAM sends 64 KiB chunks (<uint32 BE length><bytes>, then a zero-length chunk). clamd answers and closes as soon
# as the stream passes StreamMaxLength, so a failed send is expected there and the reply is read anyway.
client='
import socket, struct, sys
port, command = int(sys.argv[1]), sys.argv[2]
chunk_size = 64 * 1024
with socket.create_connection(("127.0.0.1", port), timeout=120) as s:
    s.sendall(b"z" + command.encode() + b"\0")
    if command == "INSTREAM":
        kind, value = sys.argv[3], sys.argv[4]
        try:
            if kind == "text":
                data = value.encode()
                for i in range(0, len(data), chunk_size):
                    s.sendall(struct.pack(">I", len(data[i:i + chunk_size])) + data[i:i + chunk_size])
            else:
                left, zeros = int(value), bytes(chunk_size)
                while left > 0:
                    n = min(left, chunk_size)
                    s.sendall(struct.pack(">I", n) + zeros[:n])
                    left -= n
            s.sendall(struct.pack(">I", 0))
        except (BrokenPipeError, ConnectionResetError):
            pass
    reply = b""
    try:
        while not reply.endswith(b"\0"):
            chunk = s.recv(4096)
            if not chunk:
                break
            reply += chunk
    except ConnectionResetError:
        pass
print(reply.rstrip(b"\0").decode())
'
clamd_tcp() { python3 -I -c "$client" "$(port)" "$@"; }

docker volume create "$volume" >/dev/null
if $online; then
  start
else
  # HDB signature: MD5 and size of the EICAR file. Written by the image's own user into the fresh volume.
  printf '44d88612fea8a8f36de82e1278abb02f:68:Kalla.Test.EICAR\n' |
    docker run --rm -i -v "$volume:/var/lib/clamav" --entrypoint sh "$image" -c 'cat > /var/lib/clamav/test.hdb'
  start --entrypoint clamd -- --foreground
fi
wait_clamd
ok "clamd answers zPING over TCP 3310"

uids="$(docker top "$id" -eo pid,uid | awk 'NR > 1 { print $2 }' | sort -u | tr '\n' ' ')"
[ "$uids" = "1000 " ] || fail "processes run as uid(s) $uids, expected 1000 only"
[ "$(docker inspect -f '{{.HostConfig.ReadonlyRootfs}}' "$id")" = true ] || fail "root file system is writable"
ok "every process runs as uid 1000 on a read-only root file system"

version="$(clamd_tcp VERSION)"
echo "$version" | grep -Eq '^ClamAV 1\.4\.[0-9]+(/[0-9]+/.+)?$' || fail "unexpected zVERSION reply: $version"
ok "zVERSION: $version"

reply="$(clamd_tcp INSTREAM text "$eicar")"
echo "$reply" | grep -Eiq '^stream: .*eicar.* FOUND$' || fail "EICAR not found: $reply"
ok "EICAR: $reply"

reply="$(clamd_tcp INSTREAM zeros $((50 * 1024 * 1024)))"
[ "$reply" = "stream: OK" ] || fail "clean 50 MB stream: $reply"
ok "clean 50 MB stream: $reply"

reply="$(clamd_tcp INSTREAM zeros $((60 * 1024 * 1024 + 1)))"
[ "$reply" = "INSTREAM size limit exceeded. ERROR" ] || fail "stream over 60M: $reply"
ok "stream of 60M + 1 byte: $reply"

for _ in $(seq 1 90); do
  health="$(docker inspect -f '{{.State.Health.Status}}' "$id")"
  [ "$health" = healthy ] && break
  sleep 1
done
[ "$health" = healthy ] || fail "health check: $health"
ok "health check (a clamdscan scan through the socket): healthy"

echo "info: memory $(docker exec "$id" cat /sys/fs/cgroup/memory.current) bytes ($(docker stats --no-stream --format '{{.MemUsage}}' "$id"))"

if $online; then
  before="$(docker exec "$id" sh -c 'ls -l --time-style=+%s /var/lib/clamav/main.cvd')"
  start
  wait_clamd
  docker logs "$id" 2>&1 | grep -q 'Updating initial database' && fail "the second start downloaded the signatures again"
  [ "$(docker exec "$id" sh -c 'ls -l --time-style=+%s /var/lib/clamav/main.cvd')" = "$before" ] ||
    fail "main.cvd changed on the second start"
  ok "a second container on the same volume reused the signatures: $(docker logs "$id" 2>&1 | grep -m1 'main.cvd database is up-to-date')"
fi

echo PASS
