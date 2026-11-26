#!/usr/bin/env bash
# deploy/upgrade/upgrade-to-0.9.sh on deploy/upgrade/lib.sh (Decisions "Compose host layout and upgrades", #1101 D2,
# D3; verify.yml deploy-checks), offline: `docker` is a stand-in that answers from files and records every call, so no
# container runs.
#
#   - without --apply: the plan, nothing changed (no docker call that writes, deploy/.env as it was)
#   - named volumes: the old project down, each volume copied into a new one with Compose's labels; the variables renamed
#   - data on a host disk (the VM's case: the service postgres, a direct bind, a bind-backed volume): the new volumes are
#     bind-backed volumes of the same paths, nothing copied; a backup directory its variable names is left alone
#   - no role rename: nothing signs in to Postgres (kalla-roles does it when the release starts)
#   - refusals: two old projects, a required volume missing or empty, a new volume that holds other data
#   - a re-run after it was done
#
#   deploy/tests/upgrade-0.9.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; trap 'rm -r "$work"' EXIT
fake="$work/fake"; d="$work/deploy"
mkdir -p "$fake/bin" "$d/upgrade"
cp "$root/deploy/upgrade/upgrade-to-0.9.sh" "$root/deploy/upgrade/lib.sh" "$d/upgrade/"
cp "$root/deploy/compose.rag.yaml" "$d/"

# State: $FAKE/containers (project|service|id|image), $FAKE/mounts.ID (type|name|source|destination), and one file per
# volume, $FAKE/vol/NAME, with lines project=, volume=, device=, data (holds files) and unfinished (a copy stopped).
cat > "$fake/bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
echo "docker $*" >> "$FAKE/calls"
label() { sed -n "s/^$1=//p" "$FAKE/vol/$2" 2>/dev/null; }
has_data() { # has_data MOUNT-ARG
  local src; src="$(sed -n 's/.*src=\([^,]*\).*/\1/p' <<< "$1")"
  case "$1" in
    type=volume*) dev="$(label device "$src")"
                  if [ -n "$dev" ]; then [ -n "$(ls -A "$dev" 2>/dev/null)" ]; else grep -qx data "$FAKE/vol/$src" 2>/dev/null; fi;;
    type=bind*) [ -n "$(ls -A "$src" 2>/dev/null)" ];;
  esac
}
case "$1" in
  ps)
    project=""; service=""; format=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --filter) case "$2" in label=com.docker.compose.project=*) project="${2#*project=}";; label=com.docker.compose.service=*) service="${2#*service=}";; esac; shift 2;;
        --format) format="$2"; shift 2;;
        *) shift;;
      esac
    done
    while IFS='|' read -r p s id _; do
      [ -z "$project" ] || [ "$p" = "$project" ] || continue
      [ -z "$service" ] || [ "$s" = "$service" ] || continue
      case "$format" in *ID*) echo "$id";; *) echo "$p";; esac
    done < "$FAKE/containers";;
  inspect)
    case "$3" in
      *Mounts*) cat "$FAKE/mounts.$4" 2>/dev/null || true;;
      *Config.Image*) awk -F'|' -v id="$4" '$3 == id { print $4 }' "$FAKE/containers";;
    esac;;
  volume)
    case "$2" in
      inspect)
        name="${*: -1}"; [ -e "$FAKE/vol/$name" ] || exit 1
        case "$*" in
          *device*) dev="$(label device "$name")"; echo "${dev:-<no value>}";;
          *Mountpoint*) echo "/var/lib/docker/volumes/$name/_data";;
        esac;;
      ls)
        project="$(sed -n 's/.*com.docker.compose.project=\([^ ]*\).*/\1/p' <<< "$*")"
        vol="$(sed -n 's/.*com.docker.compose.volume=\([^ ]*\).*/\1/p' <<< "$*")"
        for f in "$FAKE/vol"/*; do
          [ -e "$f" ] || continue
          n="${f##*/}"; [ "$(label project "$n")" = "$project" ] && [ "$(label volume "$n")" = "$vol" ] && echo "$n"
        done; true;;
      create)
        name="${*: -1}"; : > "$FAKE/vol/$name"
        sed -n 's/.*com.docker.compose.project=\([^ ]*\).*/project=\1/p; ' <<< "$*" >> "$FAKE/vol/$name"
        sed -n 's/.*device=\([^ ]*\).*/device=\1/p' <<< "$*" >> "$FAKE/vol/$name";;
      rm) rm "$FAKE/vol/$3";;
    esac;;
  run)
    mounts=(); prev=""; for a in "$@"; do if [ "$prev" = --mount ]; then mounts+=("$a"); fi; prev="$a"; done
    script="${*: -1}"
    case "$script" in
      *'ls -A /s'*) if has_data "${mounts[0]}"; then echo x; fi;;
      *copy-unfinished\ \]*) src="$(sed -n 's/.*src=\([^,]*\).*/\1/p' <<< "${mounts[0]}")"; if grep -qx unfinished "$FAKE/vol/$src"; then echo unfinished; fi;;
      *'cp -a'*) to="$(sed -n 's/.*src=\([^,]*\).*/\1/p' <<< "${mounts[1]}")"; sed -i '/^unfinished$/d' "$FAKE/vol/$to"; echo data >> "$FAKE/vol/$to";;
    esac;;
  compose) ;;
  *) echo "fake docker: unexpected $*" >&2; exit 3;;
esac
EOF
chmod +x "$fake/bin/docker"
export FAKE="$fake" PATH="$fake/bin:$PATH"

fail() { echo "$out"; echo "FAIL: $*"; exit 1; }
run() { # run EXPECTED-EXIT [options]: the output in $out
  local want="$1"; shift; local rc=0
  : > "$fake/calls"
  out="$(bash "$d/upgrade/upgrade-to-0.9.sh" --env-file "$d/.env" "$@" 2>&1)" || rc=$?
  [ "$rc" = "$want" ] || fail "exit $rc, expected $want ($*)"
}
has() { grep -qF -- "$1" <<< "$out" || fail "no line with: $1"; }
called() { grep -qF -- "$1" "$fake/calls" || fail "no docker call with: $1"; }
not_called() { if grep -qF -- "$1" "$fake/calls"; then fail "a docker call with: $1"; fi; }
reset() {
  rm -rf "$fake/vol" "$fake"/mounts.* "$work/srv"; mkdir -p "$fake/vol" "$work/srv"
  : > "$fake/containers"
  printf "UME_RAG_DOMAIN='umea.se'\nRAG_API_IMAGE='ghcr.io/x/api:0.8.0'\nUME_GATEWAY_URL='https://fors'\n" > "$d/.env"
  rm -f "$d/.env.pre-0.9"
}
vol() { # vol NAME PROJECT VOLUME [device=PATH] [data]
  local f="$fake/vol/$1"; printf 'project=%s\nvolume=%s\n' "$2" "$3" > "$f"; shift 3
  for a in "$@"; do echo "$a" >> "$f"; done
}
# The 0.8 project ume-rag on named volumes.
named() {
  reset
  printf 'ume-rag|rag-postgres|pg1|ghcr.io/x/postgres:0.8.0\nume-rag|rag-bootstrap|bs1|ghcr.io/x/api:0.8.0\n' > "$fake/containers"
  echo "volume|ume-rag_rag-postgres-data|/var/lib/docker/volumes/ume-rag_rag-postgres-data/_data|/var/lib/postgresql/data" > "$fake/mounts.pg1"
  : > "$fake/mounts.bs1"
  for v in certs secrets blobs openwebui-data; do
    vol "ume-rag_rag-$v" ume-rag "rag-$v" data
    echo "volume|ume-rag_rag-$v|/var/lib/docker/volumes/ume-rag_rag-$v/_data|/${v/openwebui-data/openwebui-data}" >> "$fake/mounts.bs1"
  done
  vol ume-rag_rag-postgres-data ume-rag rag-postgres-data data
}

echo "== the plan changes nothing"
named; cp "$d/.env" "$work/env-before"
run 0
has "old project: ume-rag"; has "new project: kalla"
has "would rename the variables on 3 lines"
has "rag-postgres-data: copy the volume ume-rag_rag-postgres-data to kalla_kalla-postgres-data"
has "rag-pgbackrest-repo: not in this deployment, skipped"
has "would run: docker compose -p ume-rag down"
has "Nothing was changed"
cmp -s "$d/.env" "$work/env-before" || fail "the plan changed deploy/.env"
not_called "volume create"; not_called "cp -a"; not_called "compose -p"

echo "== named volumes, applied"
run 0 --apply
for v in "KALLA_DOMAIN='umea.se'" "KALLA_API_IMAGE='ghcr.io/x/api:0.8.0'" "FORS_GATEWAY_URL='https://fors'"; do
  grep -qxF "$v" "$d/.env" || fail "the variables weren't renamed: $(cat "$d/.env")"
done
[ -e "$d/.env.pre-0.9" ] || fail "no .env.pre-0.9"
called "compose -p ume-rag down"
called "volume create --label com.docker.compose.project=kalla --label com.docker.compose.volume=kalla-certs kalla_kalla-certs"
called "--mount type=volume,src=ume-rag_rag-blobs,dst=/from,readonly --mount type=volume,src=kalla_kalla-blobs,dst=/to"
not_called "psql"; not_called "exec"
has "kalla-roles renames the database roles"

echo "== a re-run after it was done"
: > "$fake/containers"; run 0 --apply; has "steps 2 and 3 were done already"

echo "== data on a host disk (the VM's case)"
reset
mkdir -p "$work/srv/pg" "$work/srv/blobs" "$work/srv/backup"; touch "$work/srv/pg/PG_VERSION" "$work/srv/blobs/b1"
printf 'ume-rag|postgres|pg1|ghcr.io/x/postgres:0.8.0\nume-rag|rag-bootstrap|bs1|ghcr.io/x/api:0.8.0\n' > "$fake/containers"
printf 'bind||%s|/var/lib/postgresql/data\nbind||%s|/var/lib/pgbackrest\n' "$work/srv/pg" "$work/srv/backup" > "$fake/mounts.pg1"
vol ume-rag_rag-blobs ume-rag rag-blobs "device=$work/srv/blobs"
vol ume-rag_rag-certs ume-rag rag-certs data; vol ume-rag_rag-secrets ume-rag rag-secrets data
printf 'volume|ume-rag_rag-certs|x|/certs\nvolume|ume-rag_rag-secrets|x|/secrets\nvolume|ume-rag_rag-blobs|%s|/blobs\n' "$work/srv/blobs" > "$fake/mounts.bs1"
printf "UME_RAG_BACKUP_DIR='%s'\n" "$work/srv/backup" >> "$d/.env"
run 0 --apply
has "rag-postgres-data: on the host path $work/srv/pg (bind): kalla_kalla-postgres-data becomes a bind-backed volume of that path"
has "rag-blobs: on the host path $work/srv/blobs (bind-backed)"
has "rag-pgbackrest-repo: the host directory $work/srv/backup, which KALLA_BACKUP_DIR keeps naming: nothing to do"
called "volume create --label com.docker.compose.project=kalla --label com.docker.compose.volume=kalla-postgres-data --driver local --opt type=none --opt o=bind --opt device=$work/srv/pg kalla_kalla-postgres-data"
called "--opt device=$work/srv/blobs kalla_kalla-blobs"
not_called "src=$work/srv/pg,dst=/from"; not_called "src=ume-rag_rag-blobs,dst=/from"
called "src=ume-rag_rag-certs,dst=/from"
# Again: the volumes are already bound to the same paths.
printf 'ume-rag|postgres|pg1|ghcr.io/x/postgres:0.8.0\nume-rag|rag-bootstrap|bs1|ghcr.io/x/api:0.8.0\n' > "$fake/containers"
run 0 --apply; has "kalla_kalla-postgres-data is already bind-backed to $work/srv/pg: nothing to do"

echo "== refusals"
named; printf 'other|rag-postgres|pg2|ghcr.io/x/postgres:0.8.0\n' >> "$fake/containers"
run 1; has "more than one project has rag-postgres (other ume-rag): name one with --project"
run 0 --old-project ume-rag; has "old project: ume-rag"
named; sed -i '/^data$/d' "$fake/vol/ume-rag_rag-secrets"
run 1; has "rag-secrets of project ume-rag is empty"
named; rm "$fake/vol/ume-rag_rag-certs"; sed -i '/rag-certs/d' "$fake/mounts.bs1"
run 1; has "rag-certs of project ume-rag not found"
reset
mkdir -p "$work/srv/pg"; touch "$work/srv/pg/PG_VERSION"
printf 'ume-rag|postgres|pg1|ghcr.io/x/postgres:0.8.0\nume-rag|rag-bootstrap|bs1|ghcr.io/x/api:0.8.0\n' > "$fake/containers"
printf 'bind||%s|/var/lib/postgresql/data\n' "$work/srv/pg" > "$fake/mounts.pg1"
for v in certs secrets blobs; do vol "ume-rag_rag-$v" ume-rag "rag-$v" data; echo "volume|ume-rag_rag-$v|x|/$v" >> "$fake/mounts.bs1"; done
vol kalla_kalla-postgres-data kalla kalla-postgres-data data
run 1; has "kalla_kalla-postgres-data exists and holds data, but rag-postgres-data is on $work/srv/pg"
not_called "compose -p"

echo "upgrade-0.9.sh: all checks passed"
