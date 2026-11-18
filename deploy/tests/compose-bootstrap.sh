#!/usr/bin/env bash
# rag-bootstrap in its Compose form (verify.yml deploy-checks; Decisions "One deployment bootstrap"): deploy/.env from
# init-deployment.sh, then rag-bootstrap alone, twice, on throwaway volumes, as `up` runs it, and checks:
#
#   - the second run keeps every secret, owner and mode, and issues no certificate again
#   - the gateway's inputs from host directories (a fake gateway on this host): its CA is in the trust store of rag-api,
#     rag-admin and rag-worker, the manifest's keys reach each service, and a key set by hand wins
#   - without the gateway in the stack the TLS proxy (profile proxy) has no gateway blocks and the checklist names none;
#     the log ends at the setup wizard, https://rag-admin.<domain>/setup
#   - the overlays' inputs: the backup target's repo.conf and passphrase (compose.backup.yaml), the repository and the
#     upload copy owned by Postgres and the services' uid, the 24 GB Postgres settings (compose.host-24g.yaml), and
#     ClamAV's 24 GB confs with freshclam's proxy (compose.clamav-conf.yaml)
#   - the blob directories, which rag-blobs-init made before: 1654:1654, 0750
#   - the host scripts that read the volumes: escrow-keys.sh exports the escrow set and verifies it (by --env-file, and
#     by --project as for a Portainer stack), cert-expiry.sh reads the certificates
#   - escrow-keys.sh --restore into a new project's volumes before its first start: rag-bootstrap there keeps the
#     escrowed pepper, keys, role passwords, conversation id key, Data Protection certificate and CA, and the set
#     verifies against the new project
#   - --verify on media with another deployment's set too takes the project's own set, and a restore from media with
#     several other deployments' sets is refused
#
# Nothing but rag-bootstrap's image (alpine/openssl) is pulled and nothing else starts; no network is created.
#
#   deploy/tests/compose-bootstrap.sh
#
# Needs Docker with Compose v2.33.1 or later (Engine 26 or later) and openssl.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; project="ume-rag-bootstrap-$$"
mkdir -p "$work/images"; cp -R "$repo/deploy" "$work/"; cp -R "$repo/images/clamav" "$work/images/"; rm -f "$work/deploy/.env"
d="$work/deploy"
dc() { docker compose -p "$project" --env-file "$d/.env" "$@"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
cleanup() {
  dc down -v --remove-orphans >/dev/null 2>&1 || true
  for p in "$project" "$project-restored"; do
    docker ps -aq --filter "label=com.docker.compose.project=$p" | xargs -r docker rm -f >/dev/null 2>&1 || true
    docker volume ls -q --filter "label=com.docker.compose.project=$p" | xargs -r docker volume rm >/dev/null 2>&1 || true
  done
  chmod -R u+w "$work" 2>/dev/null || true; rm -rf "$work"
}
trap cleanup EXIT

# A gateway on this host as its Compose installation leaves it: its CA, and the manifest's keys of rag-api and rag-admin.
mkdir -p "$work/gw-certs" "$work/gw-secrets/Ume.Rag" "$work/escrow"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/gw-certs/ca.key" -out "$work/gw-certs/ca.pem" -days 1 \
  -subj /CN=fake-gateway-ca 2>/dev/null
printf api-key-from-manifest > "$work/gw-secrets/Ume.Rag/rag-api"; printf admin-key-from-manifest > "$work/gw-secrets/Ume.Rag/rag-admin"
printf 'worker-key-by-hand\n' > "$work/worker.key"
bash "$d/init-deployment.sh" --domain boot.test --gateway-url https://llm.boot.test --gateway-ca-file "$work/gw-certs/ca.pem" \
  --gateway-secrets-dir "$work/gw-secrets" --gateway-credential-key-file worker="$work/worker.key" \
  --profiles clamav,render,openwebui,proxy --backup-target local --host-memory 24 --freshclam-proxy proxy.boot.test:3128 \
  --yes > "$work/init.log" || { cat "$work/init.log"; fail "init-deployment.sh failed"; }

helper="$(dc config --images rag-bootstrap)"
in_helper() { docker run --rm --network none --entrypoint /bin/sh "$@"; }
vol() { echo "-v ${project}_$1:/$2:ro"; }
snapshot() {
  # shellcheck disable=SC2046 # one -v option per word
  in_helper $(vol rag-certs certs) $(vol rag-secrets secrets) "$helper" -c '
    cd / && sha256sum certs/ca.key certs/ca.pem certs/data-protection.pfx secrets/.pepper secrets/postgres/*_password \
      secrets/api/Security__* secrets/admin/Setup__Secret secrets/worker/Security__CrawlKeyPepper secrets/.render-ws-path \
      secrets/keys/rag-blob-internal secrets/pgbackrest/cipher-pass secrets/.openwebui-rag-api-key
    find certs secrets -exec stat -c "%a %u:%g %n" {} + | sort -k3'
}
run_bootstrap() {
  dc run --rm --no-deps rag-bootstrap > "$work/bootstrap-$1.log" 2>&1 || { cat "$work/bootstrap-$1.log"; fail "rag-bootstrap run $1 failed"; }
  grep -q '^rag-bootstrap: certificates and secrets ready$' "$work/bootstrap-$1.log" \
    || { cat "$work/bootstrap-$1.log"; fail "rag-bootstrap run $1 did not finish"; }
}

echo "== rag-bootstrap twice"
run_bootstrap 1; snapshot > "$work/first"
run_bootstrap 2; snapshot > "$work/second"
! grep -q 'issuing certificate' "$work/bootstrap-2.log" || fail "the second run issued certificates again"
diff -u "$work/first" "$work/second" || fail "the second run changed a secret, an owner or a mode"
ok "the second run kept every secret, owner and mode"

echo "== the gateway's inputs from host directories"
cat_in() { in_helper -v "${project}_$1:/v:ro" "$helper" -c "cat /v/$2 2>/dev/null || echo '<none>'"; }
[ "$(cat_in rag-secrets api/Gateway__Key)" = api-key-from-manifest ] || fail "rag-api's key is not the manifest's"
[ "$(cat_in rag-secrets admin/Gateway__Key)" = admin-key-from-manifest ] || fail "rag-admin's key is not the manifest's"
[ "$(cat_in rag-secrets worker/Gateway__Key)" = worker-key-by-hand ] || fail "rag-worker's key is not the one set by hand"
for svc in api admin worker; do
  cat_in rag-certs "$svc/ca.pem" | grep -qF "$(sed -n 2p "$work/gw-certs/ca.pem")" || fail "rag-$svc does not trust the gateway's CA"
done
ok "the gateway's CA is trusted and its manifest's keys reach the services; a key set by hand wins"

echo "== no gateway in the stack"
log="$work/bootstrap-2.log"
conf="$(cat_in rag-config proxy/nginx.conf)"
for h in rag chat; do grep -q "server_name $h\.boot\.test;" <<< "$conf" || fail "the proxy has no block for $h.boot.test"; done
! grep -q 'server_name llm' <<< "$conf" || fail "the proxy has a gateway block without the gateway"
! grep -qE 'UME_GATEWAY_HOST|UME_ADMIN_HOST|gateway.s CA' "$log" || fail "the checklist names the gateway: $(cat "$log")"
grep -q '^rag-bootstrap: sign-in is set in the setup wizard: https://rag-admin\.boot\.test/setup, ' "$log" \
  || fail "rag-bootstrap's log does not point at https://rag-admin.boot.test/setup"
ok "no gateway blocks and no gateway note; the log points at https://rag-admin.boot.test/setup"

echo "== the overlays' inputs"
cat_in rag-secrets pgbackrest/repo.conf | grep -qx 'repo1-type=posix' || fail "no posix repository in repo.conf"
cat_in rag-secrets pgbackrest/repo.conf | grep -q '^repo1-cipher-pass=.' || fail "no passphrase in repo.conf"
owner() { in_helper -v "${project}_$1:/v:ro" "$helper" -c "stat -c '%u:%g %a' /v/$2"; }
[ "$(owner rag-pgbackrest-repo .)" = "999:999 755" ] || fail "the repository is $(owner rag-pgbackrest-repo .), not Postgres's"
[ "$(owner rag-upload-copy . | cut -d' ' -f1)" = 1654:1654 ] || fail "the upload copy is $(owner rag-upload-copy .)"
for b in rag spool; do [ "$(owner rag-blobs "$b")" = "1654:1654 750" ] || fail "blobs/$b is $(owner rag-blobs "$b")"; done
pg="$(cat_in rag-config postgres/postgresql.conf)"
for s in 'shared_buffers = 1536MB' 'effective_cache_size = 3500MB' 'maintenance_work_mem = 1GB'; do
  grep -q "^$s" <<< "$pg" || fail "postgresql.conf lacks '$s' for a 24 GB host"
done
cat_in rag-config clamav/clamd.conf | grep -qx 'ConcurrentDatabaseReload yes' || fail "clamd.conf is not the 24 GB one"
cat_in rag-config clamav/freshclam.conf | grep -qx 'TestDatabases yes' || fail "freshclam.conf is not the 24 GB one"
cat_in rag-config clamav/freshclam.conf | grep -qx 'HTTPProxyServer proxy.boot.test' || fail "freshclam.conf has no proxy"
[ "$(owner rag-config clamav/clamd.conf | cut -d' ' -f2)" = 644 ] || fail "rag-clamav (uid 1000) cannot read clamd.conf"
ok "repo.conf, the repository's and the upload copy's owners, the blob directories, the 24 GB Postgres and ClamAV settings"

echo "== the host scripts read the volumes"
(cd "$d" && COMPOSE_PROJECT_NAME="$project" bash ./escrow-keys.sh "$work/escrow" > "$work/escrow.log") \
  || { cat "$work/escrow.log"; fail "escrow-keys.sh failed"; }
grep -q '^escrowed 12 files' "$work/escrow.log" || fail "escrow-keys.sh: $(cat "$work/escrow.log")"
(cd "$d" && COMPOSE_PROJECT_NAME="$project" bash ./escrow-keys.sh --verify "$work/escrow" > "$work/verify.log") \
  || { cat "$work/verify.log"; fail "escrow-keys.sh --verify failed"; }
grep -q '^PASS' "$work/verify.log" || fail "escrow-keys.sh --verify did not pass"
# As on a Portainer stack: the project by name, the image from its rag-bootstrap container.
dc create rag-bootstrap >/dev/null 2>&1 || fail "docker compose create rag-bootstrap failed"
bash "$d/escrow-keys.sh" --project "$project" --verify "$work/escrow" > "$work/verify-project.log" \
  || { cat "$work/verify-project.log"; fail "escrow-keys.sh --project --verify failed"; }
COMPOSE_PROJECT_NAME="$project" bash "$d/host/cert-expiry.sh" --env-file "$d/.env" --state-dir "$work/state" > "$work/certs.log" \
  || { cat "$work/certs.log"; fail "cert-expiry.sh failed on fresh certificates"; }
grep -q '^ok: postgres/postgres.crt expires' "$work/certs.log" || fail "cert-expiry.sh did not read postgres.crt"
ok "escrow-keys.sh exports the escrow set and verifies it by --env-file and by --project; cert-expiry.sh reads every certificate"

echo "== a restore from the escrow set into a new project"
restored="$project-restored"
bash "$d/escrow-keys.sh" --project "$restored" --image "$helper" --restore "$work/escrow" > "$work/restore.log" \
  || { cat "$work/restore.log"; fail "escrow-keys.sh --restore failed"; }
docker compose -p "$restored" --env-file "$d/.env" run --rm --no-deps rag-bootstrap > "$work/bootstrap-restored.log" 2>&1 \
  || { cat "$work/bootstrap-restored.log"; fail "rag-bootstrap failed on the restored volumes"; }
base() { # base PROJECT: the escrowed files and a service certificate's issuer
  in_helper -v "$1_rag-secrets:/secrets:ro" -v "$1_rag-certs:/certs:ro" "$helper" -c '
    cd / && sha256sum secrets/.pepper secrets/keys/rag-blob-internal secrets/pgbackrest/cipher-pass secrets/postgres/*_password \
      secrets/api/Security__ConversationIdKeys__k1 certs/ca.key certs/ca.pem certs/data-protection.pfx && openssl verify -CAfile certs/ca.pem certs/worker/worker.crt >/dev/null && echo issued-by-ca'
}
diff -u <(base "$project") <(base "$restored") || fail "the restored project's pepper, keys, role passwords or CA are not the escrowed ones"
grep -q '^rag-bootstrap: certificates and secrets ready$' "$work/bootstrap-restored.log" \
  || fail "rag-bootstrap did not finish on the restored volumes"
bash "$d/escrow-keys.sh" --project "$restored" --image "$helper" --verify "$work/escrow" | grep -q '^PASS' \
  || fail "the escrow set does not verify against the restored project"
in_helper -v "${restored}_rag-secrets:/secrets" "$helper" -c 'openssl rand -hex 48 > /secrets/.pepper'
out="$(bash "$d/escrow-keys.sh" --project "$restored" --image "$helper" --restore "$work/escrow" 2>&1)" \
  && fail "a restore over a different pepper was accepted"
grep -q 'rag-secrets/.pepper .* differs from the set' <<< "$out" || fail "a restore over a different pepper: $out"
ok "a restore keeps the pepper, the keys, the role passwords, the Data Protection certificate and the CA; a restore over other files is refused"

echo "== escrow media with the sets of several deployments"
# Another deployment's set, newer than this project's: --verify by DEST still takes this project's own set, and a
# restore into a project with no set there is refused, since the media hold two other deployments' sets.
own="$(find "$work/escrow" -mindepth 1 -maxdepth 1 -type d -name "ume-rag-keys-$project-*")"
cp -r "$own" "$work/escrow/ume-rag-keys-other-29991231T235959Z"
bash "$d/escrow-keys.sh" --project "$project" --verify "$work/escrow" > "$work/verify-own.log" \
  || { cat "$work/verify-own.log"; fail "--verify did not take the project's own set beside another deployment's"; }
grep -q "^verifying $own\$" "$work/verify-own.log" || fail "--verify took another set: $(head -n 1 "$work/verify-own.log")"
out="$(bash "$d/escrow-keys.sh" --project "$project-third" --image "$helper" --restore "$work/escrow" 2>&1)" \
  && fail "a restore from the sets of two other deployments was accepted"
grep -q 'sets of several deployments .*: name one set' <<< "$out" || fail "a restore from several deployments' sets: $out"
ok "the deployment's own set is taken by DEST; a restore with several other deployments' sets there needs one set"
echo PASS
