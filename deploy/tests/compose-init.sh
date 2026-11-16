#!/usr/bin/env bash
# deploy/init-deployment.sh, the writer of deploy/.env (verify.yml deploy-checks; Decisions "One deployment bootstrap",
# "Setup wizard"), offline in a copy of deploy/ with `docker compose config`:
#
#   - a first run writes deploy/.env, mode 600, with the malware scan's profile, renders, and sets no sign-in value: no
#     Oidc__* of rag-admin has a value and nothing is prompted for
#   - a re-run without options keeps every line, and the deployer's UME_RAG_SETUP_REOPEN reaches rag-admin
#   - the options: --profiles (the renderer and ClamAV only with theirs), --host-memory 24 and 16 (its overlays on and
#     off), --backup-target (a share's directories, s3:// with its credentials and endpoint), --set-file (a secret that
#     reaches rag-bootstrap), --hardened with --oidc-authority and --proxy-address (the host firewall's rules)
#   - refusals with one line: a retired variable, a service that has no credential key, a bad domain, a host name as a
#     proxy address, s3:// without credentials, --yes without the gateway's address
#   - the host firewall's subnets are compose.hardened.yaml's
#
#   deploy/tests/compose-init.sh
#
# Needs Docker with Compose v2.33.1 or later and jq.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
cp -R "$repo/deploy" "$work/"; rm -f "$work/deploy/.env"
d="$work/deploy"; env="$d/.env"
# deploy/.env is under test: an inherited variable would override it.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^(UME_|RAG_|COMPOSE_)' || true)
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
init() { bash "$d/init-deployment.sh" "$@" < /dev/null > "$work/init.log" 2>&1 || { cat "$work/init.log"; fail "init-deployment.sh $* failed"; }; }
refused() { # refused MESSAGE OPTION...: the run fails, saying MESSAGE
  if bash "$d/init-deployment.sh" "${@:2}" --yes < /dev/null > "$work/refused.log" 2>&1; then fail "accepted: ${*:2}"; fi
  grep -q -- "$1" "$work/refused.log" || fail "${*:2}: $(cat "$work/refused.log")"
}
json() { (cd / && docker compose --env-file "$env" config --format json); }
has() { grep -qx "$1" "$env" || fail "deploy/.env has no line $1"; }

echo "== a first run"
init --domain example.se --gateway-url https://llm.example.se --yes
[ "$(stat -c %a "$env")" = 600 ] || fail "deploy/.env is not mode 600"
has "COMPOSE_PROFILES='clamav'"; has "UME_DOMAIN='example.se'"; has "COMPOSE_FILE='$d/compose.rag.yaml'"
json > "$work/c.json"
jq -e '.services["rag-admin"].environment | [to_entries[] | select((.key | startswith("Oidc__")) and (.value // "") != "")] | length == 0' \
  "$work/c.json" >/dev/null || fail "rag-admin has a sign-in value without one given"
jq -e '.services | has("rag-clamav") and (has("rag-renderer") | not) and (has("rag-openwebui") | not)' "$work/c.json" >/dev/null \
  || fail "the first run's services are not the default profile's"
jq -e '.services["rag-api"].environment.Gateway__BaseUrl == "https://llm.example.se"' "$work/c.json" >/dev/null || fail "Gateway__BaseUrl"
grep -q "https://rag-admin.example.se/setup" "$work/init.log" || fail "the closing lines do not name the setup wizard"
ok "deploy/.env, mode 600, renders with the clamav profile and no sign-in value; the next steps end at the wizard"

echo "== a re-run keeps every line"
echo "UME_RAG_SETUP_REOPEN=2026-11-16" >> "$env"; cp "$env" "$work/before"
init --yes
grep -v '^#' "$work/before" | sed "s/^UME_RAG_SETUP_REOPEN=2026-11-16$/UME_RAG_SETUP_REOPEN='2026-11-16'/" | sort > "$work/a"
grep -v '^#' "$env" | sort > "$work/b"
diff -u "$work/a" "$work/b" || fail "a re-run changed deploy/.env"
json | jq -e '.services["rag-admin"].environment.Setup__Reopen == "2026-11-16"' >/dev/null || fail "Setup__Reopen"
ok "a re-run without options keeps deploy/.env; UME_RAG_SETUP_REOPEN reaches rag-admin"

echo "== options"
init --profiles clamav,render,openwebui --yes
json | jq -e '.services | has("rag-renderer") and has("rag-openwebui") and has("rag-openwebui-setup")' >/dev/null || fail "--profiles"
json | jq -e '.services["rag-worker"].environment.UME_RAG_PROFILES == "clamav,render,openwebui"' >/dev/null || fail "UME_RAG_PROFILES"
init --profiles '' --yes; has "COMPOSE_PROFILES=''"
json | jq -e '.services | (has("rag-clamav") | not) and (has("rag-renderer") | not)' >/dev/null || fail "--profiles ''"
init --profiles clamav --host-memory 24 --yes
grep -q "compose.host-24g.yaml:$d/compose.clamav-conf.yaml'$" "$env" || fail "--host-memory 24 lists no 24 GB overlays"
json | jq -e '.services["rag-bootstrap"].environment.UME_RAG_HOST_MEMORY == "24" and .services["rag-clamav"].mem_limit == "3221225472"' \
  >/dev/null || fail "the 24 GB overlays do not render"
init --host-memory 16 --yes; has "COMPOSE_FILE='$d/compose.rag.yaml'"
init --backup-target /mnt/backup/ume-rag/ --yes
has "UME_RAG_BACKUP_DIR='/mnt/backup/ume-rag/pgbackrest'"; has "UME_RAG_UPLOAD_BACKUP_DIR='/mnt/backup/ume-rag/uploads'"
json | jq -e '.services["rag-postgres"].volumes | map(.target) | index("/var/lib/pgbackrest")' >/dev/null || fail "the backup overlay"
printf 'ACCESS\nSECRET\n' > "$work/s3"
init --backup-target s3://s3.example.se/bucket/rag --backup-s3-credentials-file "$work/s3" --yes
has "UME_RAG_BACKUP_ENDPOINT='s3.example.se'"; has "UME_RAG_BACKUP_S3_KEY_SECRET='SECRET'"
! grep -q '^UME_RAG_BACKUP_DIR=' "$env" || fail "an S3 target kept the share's directory"
printf 'client-secret\n' > "$work/secret"
init --set-file UME_RAG_OIDC_CLIENT_SECRET="$work/secret" --set UME_RAG_OIDC_CLIENT_ID=rag-admin --yes
json | jq -e '.services["rag-bootstrap"].environment.UME_RAG_OIDC_CLIENT_SECRET == "client-secret"
  and .services["rag-admin"].environment.Oidc__ClientId == "rag-admin"' >/dev/null || fail "--set-file and --set"
init --set UME_RAG_OIDC_CLIENT_ID= --yes; ! grep -q '^UME_RAG_OIDC_CLIENT_ID=' "$env" || fail "--set NAME= keeps the line"
init --hardened --oidc-authority https://192.0.2.10/realms/ume --proxy-address 10.1.2.3 --profiles clamav,openwebui \
  --gateway-url https://192.0.2.20 --backup-target local --yes
grep -q "compose.hardened.yaml" "$env" || fail "--hardened"
grep -q "deploy/host/install.sh" "$work/init.log" || fail "--hardened does not name install.sh"
rules="$(bash "$d/host/docker-user-firewall.sh" --dry-run --env-file "$env" --dns 192.0.2.53 2>&1)" \
  || { echo "$rules"; fail "the firewall's dry run"; }
for rule in '-A UME-RAG-EGRESS -s 10.1.2.3 -p tcp -m conntrack --ctstate DNAT --ctorigdstport 8543 -j RETURN' \
            '-A UME-RAG-EGRESS -p tcp -m conntrack --ctstate DNAT --ctorigdstport 3000 -j REJECT' \
            '-A UME-RAG-EGRESS -s 172.30.1.0/24 -d 192.0.2.10 -p tcp --dport 443 -j RETURN' \
            '-A UME-RAG-EGRESS -s 172.30.3.0/24 -d 192.0.2.10 -p tcp --dport 443 -j RETURN'; do
  grep -qF -- "$rule" <<< "$rules" || fail "the firewall lacks: $rule"
done
ok "profiles, the 24 GB overlays on and off, the backup targets, --set and --set-file, --hardened and the firewall's rules"

echo "== the gateway's CA certificate, and a host without the slice"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$work/gw-ca.key" -out "$work/gw-ca.pem" -days 1 -subj /CN=gw-ca 2>/dev/null
init --gateway-ca-file "$work/gw-ca.pem" --set UME_RAG_INGESTION_SLICE= --yes
cmp -s "$work/gw-ca.pem" "$d/gateway-ca/ca.pem" || fail "--gateway-ca-file did not copy the certificate to deploy/gateway-ca/ca.pem"
[ "$(ls "$d/gateway-ca")" = ca.pem ] || fail "deploy/gateway-ca holds more than the certificate"
grep -qxF "UME_RAG_GATEWAY_CA_DIR='$d/gateway-ca'" "$env" || fail "UME_RAG_GATEWAY_CA_DIR does not name deploy/gateway-ca"
grep -qxF "UME_RAG_INGESTION_SLICE=''" "$env" || fail "an empty UME_RAG_INGESTION_SLICE was not written"
init --yes
grep -qxF "UME_RAG_INGESTION_SLICE=''" "$env" || fail "a re-run dropped the empty UME_RAG_INGESTION_SLICE"
if bash "$d/init-deployment.sh" --gateway-ca-file "$work/gw-ca.key" --yes < /dev/null > "$work/init.log" 2>&1; then
  fail "a private key was accepted as the gateway's CA certificate"
fi
ok "--gateway-ca-file copies the certificate alone and refuses a key; an empty UME_RAG_INGESTION_SLICE survives a re-run"

echo "== refusals"
refused 'UME_RAG_CLAMAV_MODE is no longer read' --set UME_RAG_CLAMAV_MODE=on
refused 'SERVICE is api, admin or worker' --gateway-credential-key-file parser="$work/secret"
refused 'UME_DOMAIN takes a host or domain name' --domain .example.se
refused 'takes IPv4 addresses or CIDRs' --proxy-address proxy.example.se
refused 'needs --backup-s3-credentials-file' --set UME_RAG_BACKUP_S3_KEY= --backup-target s3://s3.example.se/b
refused 'unknown profile' --profiles clamav,chat
rm "$env"; refused "The gateway's URL (https://...) is required" --domain example.se
ok "a retired variable, a parser key, a bad domain, a proxy's host name, s3:// without credentials, an unknown profile and a missing gateway"

echo "== the host firewall's subnets are compose.hardened.yaml's"
init --domain example.se --gateway-url https://llm.example.se --backup-target local --hardened --profiles clamav,render --yes
json | jq -r '.networks | to_entries[] | "\(.key) \(.value.ipam.config[0].subnet)"' | sort > "$work/subnets"
bash "$d/host/docker-user-firewall.sh" --print-subnets | sort | diff -u "$work/subnets" - || fail "the subnets differ"
ok "every network of the hardened stack has the firewall's subnet"
bash "$d/init-deployment.sh" --help | grep -q -- '--backup-target TARGET' || fail "--help does not list the options"
echo PASS
