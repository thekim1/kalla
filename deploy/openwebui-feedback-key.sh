#!/usr/bin/env bash
# The feedback service account's API key (chat-api/design.md §4.3, §13.3, §14.2): rag-api's feedback importer reads
# Open WebUI's feedback list and deletes feedback past retention with it, as the secret file OpenWebUi__AdminApiKey.
#
#   sudo deploy/openwebui-feedback-key.sh [--env-file FILE] [--url URL]
#
# The account is Open WebUI's first user, and so its only admin: init-deployment.sh --openwebui-host writes its
# credentials (WEBUI_ADMIN_EMAIL, WEBUI_ADMIN_PASSWORD) into secrets/openwebui.env, and Open WebUI creates it when it
# starts on an empty store. Password sign-in is off in compose.openwebui.yaml, so this script:
#
#   1. recreates the open-webui service with UME_RAG_OPENWEBUI_PASSWORD_AUTH=true;
#   2. signs in as the account (it must have the role admin) and mints its API key (a new key replaces the old one,
#      so a re-run rotates it);
#   3. checks the key: the feedback list answers 200, a route outside API_KEYS_ALLOWED_ENDPOINTS answers 403;
#   4. writes it to secrets/api/OpenWebUi__AdminApiKey (owned by uid 1654 when run as root);
#   5. always recreates the service with password sign-in off again and checks that a sign-in is refused.
#
# Then restart rag-api so it reads the new file: docker compose --env-file deploy/.env restart rag-api
#
# Options:
#   --env-file FILE    [deploy/.env] the deployment (UME_RAG_SECRETS_DIR, UME_RAG_OPENWEBUI_PORT)
#   --url URL          [http://127.0.0.1:<UME_RAG_OPENWEBUI_PORT>] where this host reaches Open WebUI
#
# Needs Docker with Compose v2, curl and jq.
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; url=""

usage() { sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) env_file="${2:-}"; shift 2;;
    --url) url="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
for tool in docker curl jq; do command -v "$tool" >/dev/null || die "$tool is required"; done
[ -r "$env_file" ] || die "cannot read $env_file: run deploy/init-deployment.sh first, or pass --env-file"
env_file="$(cd "$(dirname "$env_file")" && pwd)/$(basename "$env_file")"

setting() { # setting FILE KEY — the last KEY= value in FILE, without surrounding single quotes
  local v
  v="$(sed -n "s/^$2=//p" "$1" | tail -n 1)"
  v="${v#\'}"; printf '%s' "${v%\'}"
}
secrets="$(setting "$env_file" UME_RAG_SECRETS_DIR)"
if [ -z "$secrets" ] || [ ! -r "$secrets/openwebui.env" ]; then
  die "no readable openwebui.env under UME_RAG_SECRETS_DIR ($secrets): run init-deployment.sh with --openwebui-host"
fi
email="$(setting "$secrets/openwebui.env" WEBUI_ADMIN_EMAIL)"
password="$(setting "$secrets/openwebui.env" WEBUI_ADMIN_PASSWORD)"
if [ -z "$email" ] || [ -z "$password" ]; then
  die "WEBUI_ADMIN_EMAIL or WEBUI_ADMIN_PASSWORD missing in $secrets/openwebui.env: re-run init-deployment.sh"
fi
port="$(setting "$env_file" UME_RAG_OPENWEBUI_PORT)"
url="${url:-http://127.0.0.1:${port:-3000}}"; url="${url%/}"

owui() { docker compose -f "$here/compose.openwebui.yaml" --env-file "$env_file" "$@"; }
tmp="$(mktemp -d)"
signin() { # signin — prints the HTTP status, the response in $tmp/signin
  jq -n --arg email "$email" --arg password "$password" '{email: $email, password: $password}' |
    curl -sS -o "$tmp/signin" -w '%{http_code}' -X POST -H 'Content-Type: application/json' --data-binary @- \
      "$url/api/v1/auths/signin"
}
restore() {
  local status=$?
  trap - EXIT
  echo "recreating open-webui with password sign-in off"
  if UME_RAG_OPENWEBUI_PASSWORD_AUTH=false owui up -d --wait open-webui; then
    code="$(signin)" || code=000
    if [ "$code" = 403 ]; then echo "ok: password sign-in refused (403)"
    else echo "error: password sign-in answered $code after the recreate, expected 403" >&2; status=1; fi
  else
    echo "error: could not recreate open-webui: check it by hand" >&2; status=1
  fi
  rm -rf "$tmp"
  exit "$status"
}
trap restore EXIT

echo "recreating open-webui with password sign-in on"
UME_RAG_OPENWEBUI_PASSWORD_AUTH=true owui up -d --wait open-webui

code="$(signin)"
[ "$code" = 200 ] || die "sign-in as $email answered $code: $(head -c 300 "$tmp/signin")"
role="$(jq -r '.role' "$tmp/signin")"
[ "$role" = admin ] || die "$email has the role '$role', not admin: the store had users before the account was" \
  "created (create it as an admin through POST /api/v1/auths/add, docs/test-host.md)"
# Bearer tokens and keys go to curl through header files, never on its command line.
printf 'Authorization: Bearer %s\n' "$(jq -r '.token' "$tmp/signin")" > "$tmp/session"
code="$(curl -sS -o "$tmp/key" -w '%{http_code}' -X POST -H @"$tmp/session" "$url/api/v1/auths/api_key")"
[ "$code" = 200 ] || die "minting the API key answered $code: $(head -c 300 "$tmp/key")"
key="$(jq -r '.api_key' "$tmp/key")"
[[ "$key" =~ ^sk-[0-9a-f]{32}$ ]] || die "unexpected API key format from Open WebUI"
printf 'Authorization: Bearer %s\n' "$key" > "$tmp/apikey"

code="$(curl -sS -o /dev/null -w '%{http_code}' -H @"$tmp/apikey" "$url/api/v1/evaluations/feedbacks/list?page=1")"
[ "$code" = 200 ] || die "the key cannot read the feedback list ($code)"
echo "ok: the key reads the feedback list (200)"
code="$(curl -sS -o /dev/null -w '%{http_code}' -H @"$tmp/apikey" "$url/api/v1/chats/all")"
[ "$code" = 403 ] || die "the key reached /api/v1/chats/all ($code, expected 403): check API_KEYS_ALLOWED_ENDPOINTS"
echo "ok: the key is refused outside the feedback routes (403)"

mkdir -p "$secrets/api"
printf '%s' "$key" > "$secrets/api/OpenWebUi__AdminApiKey"
if [ "$(id -u)" = 0 ]; then
  chown 1654:1654 "$secrets/api/OpenWebUi__AdminApiKey"
else
  echo "not root: run  chown 1654:1654 $secrets/api/OpenWebUi__AdminApiKey  so rag-api can read it"
fi
echo "ok: wrote $secrets/api/OpenWebUi__AdminApiKey; restart rag-api to read it"
