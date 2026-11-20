#!/usr/bin/env bash
# Test of deploy/kubernetes/create-secrets.sh --generate-only (verify.yml deploy-checks; Decisions "One deployment
# bootstrap"): the Kubernetes installation's certificates and secrets come from deploy/portainer/bootstrap.sh, run on
# the operator's machine. No cluster and no kubectl; it needs bash and openssl only. Checks:
#
#   - nothing of Open WebUI is generated without the openwebui profile
#   - a first run writes what the manifests' secrets hold: the connection strings to the host `postgres` (and to
#     --postgres-host), certificates for the host names under --domain, the credential key of a service, the declared
#     client secret, the S3 repository settings, the worker's Render__Endpoint only with --renderer
#   - --ca-file reaches the trust store of kalla-api, kalla-admin, kalla-worker and kalla-migrate; your own certificates
#     (--api-cert ...) are used instead of the internal CA's
#   - a second run with none of the value options keeps every secret and every value: the keys, the client secret, the
#     repository settings, the certificates
#   - escrow-keys.sh on the directories: the set verifies against them, a restore into new directories followed by a
#     first run keeps the pepper, the blob key, the passphrase, the role passwords, the conversation id key, the Data
#     Protection certificate and the CA (the new
#     certificates are the same CA's), the set verifies there too, and a restore over a different file is refused
#   - a missing host name, and --datastores without a repository, stop with one line
#
#   deploy/tests/kubernetes-secrets.sh
set -euo pipefail
export LC_ALL=C
here="$(cd "$(dirname "$0")" && pwd)"
deploy="$here/.."
t="$(mktemp -d)"; trap 'chmod -R u+w "$t"; rm -rf "$t"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
sums() { (cd "$1" && find certs secrets -type f | LC_ALL=C sort | xargs sha256sum); }

printf 'api-key\n' > "$t/api.key"; printf 'worker-key\n' > "$t/worker.key"; printf 'client-secret\n' > "$t/oidc.secret"
printf 'ACCESS\nSECRET\n' > "$t/s3.cred"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$t/other-ca.key" -out "$t/other-ca.pem" -days 1 -subj /CN=other-ca 2>/dev/null
for n in api admin; do
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$t/$n-own.key" -out "$t/$n-own.crt" -days 1 -subj "/CN=own-$n" 2>/dev/null
done
run() { bash "$deploy/kubernetes/create-secrets.sh" --generate-only --certs-dir "$t/k/certs" --secrets-dir "$t/k/secrets" "$@"; }
c="$t/k/certs"; s="$t/k/secrets"

echo "== first run"
run --domain example.se --gateway-credential-key-file api="$t/api.key" --oidc-client-secret-file "$t/oidc.secret" \
  --oidc-refuse-guests false --ca-file "$t/other-ca.pem" --datastores --backup-target s3://s3.example.se/bucket/rag \
  --backup-s3-credentials-file "$t/s3.cred" --renderer > "$t/first.log" 2>&1 || { cat "$t/first.log"; fail "the first run failed"; }
for svc in api admin worker migrations; do
  grep -q '^Host=postgres;Database=ragdb;Username=kalla_' "$s/$svc/ConnectionStrings__ragdb" || fail "$svc: the connection string is not to the host postgres"
done
san() { openssl x509 -in "$1" -noout -ext subjectAltName | tr -d ' \n'; }
[[ "$(san "$c/postgres/postgres.crt")" == *DNS:postgres,DNS:localhost* ]] || fail "postgres' certificate is not for the host postgres"
[[ "$(san "$c/api/api.crt")" == *DNS:rag.example.se* ]] || fail "kalla-api's certificate lacks rag.example.se"
[[ "$(san "$c/admin/admin.crt")" == *DNS:kalla-admin.example.se* ]] || fail "kalla-admin's certificate lacks kalla-admin.example.se"
[ "$(cat "$s/api/Gateway__Key")" = api-key ] || fail "kalla-api's credential key"
[ ! -e "$s/admin/Gateway__Key" ] && [ ! -e "$s/worker/Gateway__Key" ] || fail "a credential key nobody gave"
grep -q 'no credential key for admin worker' "$t/first.log" || fail "the missing keys are not named"
[ "$(cat "$s/admin/Oidc__ClientSecret")" = client-secret ] || fail "the client secret"
[ "$(cat "$s/admin/Oidc__RefuseGuests")" = false ] || fail "Oidc__RefuseGuests"
[ -s "$s/admin/Setup__Secret" ] || fail "kalla-admin's setup secret"
for line in repo1-s3-bucket=bucket repo1-path=/rag repo1-s3-key=ACCESS repo1-s3-key-secret=SECRET; do
  grep -qx "$line" "$s/pgbackrest/repo.conf" || fail "the pgBackRest repository settings lack $line"
done
grep -q '^ws://kalla-renderer:3000/[0-9a-f]\{64\}$' "$s/worker/Render__Endpoint" || fail "the worker's Render__Endpoint"
[ "/$(basename "$(cat "$s/worker/Render__Endpoint")")" = "$(cat "$s/renderer/ws-path")" ] || fail "the renderer's path is not the endpoint's"
for svc in api admin worker migrations; do
  grep -qF "$(sed -n 2p "$t/other-ca.pem")" "$c/$svc/ca.pem" || fail "$svc does not trust the CA of --ca-file"
done
! grep -qF "$(sed -n 2p "$t/other-ca.pem")" "$c/parser/ca.pem" || fail "the parser trusts the CA of --ca-file"
# Without the openwebui profile (Kubernetes has no Open WebUI) nothing of Open WebUI is generated.
[ ! -e "$s/.openwebui-jwt-secret" ] && [ ! -e "$s/.openwebui-rag-api-key" ] && [ ! -e "$s/api/OpenWebUi__JwtSecret" ] \
  && [ -z "$(ls -A "$s/openwebui")" ] || fail "an Open WebUI secret was generated without the openwebui profile"
ok "the first run wrote the connection strings, the certificates, the key, the declared sign-in, the repository and the trust"
sums "$t/k" > "$t/sums1"

echo "== second run, no value options"
run --domain example.se --ca-file "$t/other-ca.pem" --datastores --renderer > "$t/second.log" 2>&1 || { cat "$t/second.log"; fail "the second run failed"; }
sums "$t/k" > "$t/sums2"
diff "$t/sums1" "$t/sums2" > "$t/diff" || { cut -c1-2,67- "$t/diff"; fail "the second run changed a file"; }
! grep -q 'issuing certificate' "$t/second.log" || fail "the second run issued a certificate"
ok "the second run kept every secret, key, certificate and the repository settings ($(wc -l < "$t/sums1") files)"

echo "== options that change something"
run --domain example.se --postgres-host db.example.se --gateway-credential-key-file worker="$t/worker.key" \
  --api-cert "$t/api-own.crt" --api-key "$t/api-own.key" --admin-cert "$t/admin-own.crt" --admin-key "$t/admin-own.key" \
  > "$t/third.log" 2>&1 || { cat "$t/third.log"; fail "the third run failed"; }
grep -q '^Host=db.example.se;' "$s/api/ConnectionStrings__ragdb" || fail "--postgres-host is not in the connection string"
[ "$(cat "$s/worker/Gateway__Key")" = worker-key ] && [ "$(cat "$s/api/Gateway__Key")" = api-key ] || fail "the worker's new key, or kalla-api's kept one"
cmp -s "$c/api/api.crt" "$t/api-own.crt" || fail "your own certificate for kalla-api is not used"
cmp -s "$c/admin/admin.key" "$t/admin-own.key" || fail "your own key for kalla-admin is not used"
[ ! -e "$s/worker/Render__Endpoint" ] || fail "Render__Endpoint without --renderer"
grep -Ev '^[0-9a-f]+  (certs/(api|admin|postgres)/|secrets/[a-z]+/ConnectionStrings__ragdb|secrets/worker/(Gateway__Key|Render__Endpoint)|certs/(worker|migrations)/ca\.pem|secrets/openwebui/ca\.pem)' "$t/sums1" > "$t/kept1"
sums "$t/k" | grep -Ev '^[0-9a-f]+  (certs/(api|admin|postgres)/|secrets/[a-z]+/ConnectionStrings__ragdb|secrets/worker/(Gateway__Key|Render__Endpoint)|certs/(worker|migrations)/ca\.pem|secrets/openwebui/ca\.pem)' > "$t/kept3"
diff "$t/kept1" "$t/kept3" > "$t/diff" || { cut -c1-2,67- "$t/diff"; fail "the third run changed a file its options do not name"; }
ok "a database host, a new key and your own certificates take effect; nothing else changed"

echo "== escrow and restore"
escrow() { bash "$deploy/escrow-keys.sh" "$@"; }
mkdir "$t/escrow"
escrow --secrets-dir "$s" --certs-dir "$c" "$t/escrow" > "$t/escrow.log" || { cat "$t/escrow.log"; fail "the escrow failed"; }
grep -q '^escrowed 12 files' "$t/escrow.log" || fail "the escrow did not write 12 files: $(cat "$t/escrow.log")"
escrow --secrets-dir "$s" --certs-dir "$c" --verify "$t/escrow" | grep -q '^PASS' || fail "the set does not verify against its source"
escrow --secrets-dir "$t/r/secrets" --certs-dir "$t/r/certs" --restore "$t/escrow" > "$t/restore.log" \
  || { cat "$t/restore.log"; fail "the restore failed"; }
bash "$deploy/kubernetes/create-secrets.sh" --generate-only --certs-dir "$t/r/certs" --secrets-dir "$t/r/secrets" \
  --domain example.se --datastores --backup-target s3://s3.example.se/bucket/rag --backup-s3-credentials-file "$t/s3.cred" \
  > "$t/restored.log" 2>&1 || { cat "$t/restored.log"; fail "the first run after the restore failed"; }
for f in secrets/.pepper secrets/keys/rag-blob-internal secrets/pgbackrest/cipher-pass secrets/postgres/{bootstrap,migrator,api,admin,worker}_password \
  secrets/api/Security__ConversationIdKeys__k1 certs/data-protection.pfx certs/ca.pem certs/ca.key; do
  cmp -s "$t/k/$f" "$t/r/$f" || fail "$f is not the escrowed one after the restore and a first run"
done
openssl verify -CAfile "$c/ca.pem" "$t/r/certs/worker/worker.crt" >/dev/null 2>&1 || fail "the restored deployment's certificates are not the escrowed CA's"
escrow --secrets-dir "$t/r/secrets" --certs-dir "$t/r/certs" --verify "$t/escrow" | grep -q '^PASS' || fail "the set does not verify after the restore"
openssl rand -hex 48 > "$t/r/secrets/.pepper"
out="$(escrow --secrets-dir "$t/r/secrets" --certs-dir "$t/r/certs" --restore "$t/escrow" 2>&1)" && fail "a restore over a different pepper was accepted"
grep -q 'kalla-secrets/.pepper .* differs from the set' <<< "$out" || fail "a restore over a different pepper: $out"
ok "the escrow set restores the pepper, the keys, the role passwords, the conversation id key, the Data Protection certificate and the CA; a different file is refused"

echo "== refusals"
out="$(bash "$deploy/kubernetes/create-secrets.sh" --generate-only --certs-dir "$t/n/certs" --secrets-dir "$t/n/secrets" 2>&1)" && fail "no host name was accepted"
grep -q -- '--domain or --api-host is required' <<< "$out" || fail "no host name: $out"
out="$(bash "$deploy/kubernetes/create-secrets.sh" --generate-only --certs-dir "$t/n/certs" --secrets-dir "$t/n/secrets" --api-host a.example.se --datastores 2>&1)" && fail "--datastores without a repository was accepted"
grep -q -- '--datastores needs a pgBackRest repository' <<< "$out" || fail "--datastores without a repository: $out"
ok "no host name, and --datastores without a repository, stop with one line"
