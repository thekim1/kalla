#!/usr/bin/env bash
# deploy/host/install.sh writes host.env, which the units read (Decisions "Compose host layout and upgrades", #1101 D6),
# and --check finds units or a host.env that aren't this bundle's (verify.yml deploy-checks). Into directories only:
# no systemd, no docker.
#
#   - every service unit reads host.env and names no deploy directory in its ExecStart
#   - host.env names this deploy/ directory and the project: --project, else deploy/.env's, else kalla
#   - --check: 0 on what install.sh wrote; 1 on a changed unit, a missing host.env, another directory or another project
#   - an upgrade: install.sh from a new bundle moves host.env to the new directory, and the units stay the same
#   - KALLA_ENV_FILE: the state directory's .env once it exists, else the bundle's deploy/.env
#
#   deploy/tests/host-install.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; trap 'rm -r "$work"' EXIT
fail() { echo "FAIL: $*"; exit 1; }
bundle() { # bundle DIR: a copy of deploy/host/ under DIR/deploy, with a .env
  mkdir -p "$1/deploy"; cp -r "$root/deploy/host" "$1/deploy/"
  printf "COMPOSE_FILE='compose.rag.yaml:compose.backup.yaml'\n" > "$1/deploy/.env"
}
units="$work/units"; export KALLA_STATE_DIR="$work/state"
hv() { sed -n "s/^$1=//p" "$KALLA_STATE_DIR/host.env" | sed -e "s/^'\(.*\)'$/\1/"; }
check() { # check EXPECTED-EXIT DEPLOY [options]
  local want="$1" dir="$2"; shift 2; local rc=0
  out="$(bash "$dir/deploy/host/install.sh" --unit-dir "$units" --check "$@" 2>&1)" || rc=$?
  [ "$rc" = "$want" ] || { echo "$out"; fail "--check exit $rc, expected $want ($*)"; }
}

echo "== install"
bundle "$work/a"
bash "$work/a/deploy/host/install.sh" --unit-dir "$units" --no-enable >/dev/null
for u in "$units"/rag-*.service; do
  grep -qx "EnvironmentFile=$KALLA_STATE_DIR/host.env" "$u" || fail "${u##*/} doesn't read host.env"
  if grep '^ExecStart=' "$u" | grep -q "$work"; then fail "${u##*/} names a deploy directory in ExecStart"; fi
done
[ "$(hv KALLA_DEPLOY_DIR)" = "$work/a/deploy" ] || fail "host.env: KALLA_DEPLOY_DIR is $(hv KALLA_DEPLOY_DIR)"
[ "$(hv COMPOSE_PROJECT_NAME)" = kalla ] || fail "host.env: the default project is $(hv COMPOSE_PROJECT_NAME)"
[ "$(hv KALLA_ENV_FILE)" = "$work/a/deploy/.env" ] || fail "host.env: KALLA_ENV_FILE is $(hv KALLA_ENV_FILE), not the bundle's .env"
# shellcheck disable=SC2016 # the unit's own $$, read literally
grep -q '"$$KALLA_ENV_FILE"' "$units/rag-backup.service" || fail "rag-backup.service doesn't read KALLA_ENV_FILE"
check 0 "$work/a"; grep -q "acting on project kalla" <<< "$out" || fail "--check: $out"

echo "== the project"
printf "COMPOSE_PROJECT_NAME='ume-rag'\n" >> "$work/a/deploy/.env"
check 1 "$work/a"; grep -q "the units act on project kalla, not ume-rag" <<< "$out" || fail "--check: $out"
bash "$work/a/deploy/host/install.sh" --unit-dir "$units" --no-enable >/dev/null
[ "$(hv COMPOSE_PROJECT_NAME)" = ume-rag ] || fail "host.env: deploy/.env's project not taken"
bash "$work/a/deploy/host/install.sh" --unit-dir "$units" --no-enable --project other >/dev/null
[ "$(hv COMPOSE_PROJECT_NAME)" = other ] || fail "host.env: --project not taken"
check 0 "$work/a" --project other

echo "== a changed unit, a missing host.env"
sed -i 's/^Nice=10$/Nice=0/' "$units/rag-backup.service"
check 1 "$work/a" --project other; grep -q "rag-backup.service: not this bundle's" <<< "$out" || fail "--check: $out"
bash "$work/a/deploy/host/install.sh" --unit-dir "$units" --no-enable --project other >/dev/null
mv "$KALLA_STATE_DIR/host.env" "$work/host.env"
check 1 "$work/a" --project other; grep -q "host.env: missing" <<< "$out" || fail "--check: $out"
mv "$work/host.env" "$KALLA_STATE_DIR/host.env"

echo "== an upgrade: a new bundle beside the old one"
bundle "$work/b"; printf "COMPOSE_PROJECT_NAME='other'\n" >> "$work/b/deploy/.env"
check 1 "$work/b"; grep -q "the units run from $work/a/deploy, not $work/b/deploy" <<< "$out" || fail "--check: $out"
cp -r "$units" "$work/units-before"
bash "$work/b/deploy/host/install.sh" --unit-dir "$units" --no-enable >/dev/null
[ "$(hv KALLA_DEPLOY_DIR)" = "$work/b/deploy" ] || fail "host.env didn't move to the new bundle"
diff <(cd "$work/units-before" && grep -rv '^Documentation=' . | sort) \
  <(cd "$units" && grep -rv '^Documentation=' . | sort) >/dev/null || fail "the units changed with the directory"
check 0 "$work/b"

echo "== the state directory's env file"
cp "$work/b/deploy/.env" "$KALLA_STATE_DIR/.env"
check 1 "$work/b"; grep -q "the units read $work/b/deploy/.env, not $KALLA_STATE_DIR/.env" <<< "$out" || fail "--check: $out"
bash "$work/b/deploy/host/install.sh" --unit-dir "$units" --no-enable >/dev/null
[ "$(hv KALLA_ENV_FILE)" = "$KALLA_STATE_DIR/.env" ] || fail "host.env: KALLA_ENV_FILE is $(hv KALLA_ENV_FILE), not the state directory's"
check 0 "$work/b"

echo "host-install.sh: all checks passed"
