#!/usr/bin/env bash
# Open WebUI's settings are written twice (#707): open-webui in deploy/compose.openwebui.yaml and rag-openwebui in the
# Portainer stack (deploy/portainer/compose.rag.template.yaml, generated into compose.portainer.yaml). Offline, with
# `docker compose config --format json` of both and the same placeholder settings, this checks that:
#
#   - both have the same environment keys, apart from the deployment-specific keys listed below;
#   - every key both have renders to the same value (the security settings among them: API_KEYS_ALLOWED_ENDPOINTS,
#     ENABLE_PASSWORD_AUTH, ENABLE_SIGNUP, USER_PERMISSIONS_*, ENABLE_ADMIN_EXPORT, ...), apart from the keys listed
#     below whose value is the deployment's;
#   - neither passes OIDC settings to Open WebUI, and both start it through deploy/openwebui/start-with-sign-in.py,
#     which takes them from rag-api (identity design §19);
#   - the feedback service account the stack names is the one init-deployment.sh writes into Compose's env file;
#   - each listed key still is what its list says, so the lists don't go stale.
#
#   deploy/tests/openwebui-parity.sh
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per check and PASS, or "FAIL: ..." and exits 1.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
d="$repo/deploy"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

# Only in the Portainer stack. Compose's open-webui reaches rag-api through the reverse proxy's public certificate, so
# it needs no CA bundle; it takes the feedback service account from its env file (written by init-deployment.sh), where
# the stack has no file and sets it here.
only_portainer=(SSL_CERT_FILE REQUESTS_CA_BUNDLE WEBUI_ADMIN_EMAIL WEBUI_ADMIN_NAME)
# Only in Compose's file.
only_compose=()
# In both, with the deployment's value: Compose calls rag-api at its public URL, the stack inside the stack (the API
# connection and the start wrapper's sign-in settings route).
own_value=(OPENAI_API_BASE_URLS UME_RAG_SIGN_IN_URL)
# The OIDC settings come from the start wrapper (rag-api's sign-in settings, identity design §19), never from either file.
from_wrapper=(ENABLE_OAUTH_SIGNUP OAUTH_CLIENT_ID OAUTH_CLIENT_SECRET OAUTH_CODE_CHALLENGE_METHOD OPENID_PROVIDER_URL OAUTH_PROVIDER_NAME)

# The same placeholder settings for both (verify.yml's "Production Compose files are valid"); the stack's other
# required variables come from stack.env.example, and the variables set here win over it.
# The OIDC variables are set so that a file still passing them to Open WebUI would show.
export UME_RAG_SECRETS_DIR="$work/secrets" UME_RAG_API_PUBLIC_URL=https://rag-api.example.se
export UME_RAG_OPENWEBUI_HOST=chat.example.se UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=open-webui
export UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL=https://keycloak.example.se/realms/ume/.well-known/openid-configuration
export UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=parity-client-secret UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME=Parity
export UME_RAG_OPENWEBUI_DATA_DIR=/openwebui-data
unset OPENWEBUI_IMAGE
mkdir -p "$UME_RAG_SECRETS_DIR" && touch "$UME_RAG_SECRETS_DIR/openwebui.env"

docker compose -f "$d/compose.openwebui.yaml" config --format json \
  | jq '.services["open-webui"].environment' > "$work/compose.json"
docker compose -f "$d/portainer/compose.portainer.yaml" --env-file "$d/portainer/stack.env.example" \
  --profile openwebui config --format json | jq '.services["rag-openwebui"].environment' > "$work/portainer.json"

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
list() { jq -nc '$ARGS.positional' --args "$@"; }

# One line per finding: "KIND KEY".
jq -r -n --slurpfile c "$work/compose.json" --slurpfile p "$work/portainer.json" \
  --argjson only_portainer "$(list "${only_portainer[@]}")" --argjson only_compose "$(list "${only_compose[@]}")" \
  --argjson own_value "$(list "${own_value[@]}")" '
  $c[0] as $c | $p[0] as $p | ($c | keys) as $ck | ($p | keys) as $pk
  | (($ck - $pk - $only_compose)[] | "missing-in-portainer \(.)"),
    (($pk - $ck - $only_portainer)[] | "missing-in-compose \(.)"),
    ($only_portainer[] | . as $k | select(($p | has($k) | not) or ($c | has($k))) | "stale-only-portainer \($k)"),
    ($only_compose[] | . as $k | select(($c | has($k) | not) or ($p | has($k))) | "stale-only-compose \($k)"),
    ($own_value[] | . as $k | select(($c | has($k) | not) or ($p | has($k) | not) or $c[$k] == $p[$k]) | "stale-own-value \($k)"),
    (($ck - ($ck - $pk) - $own_value)[] | . as $k | select($c[$k] != $p[$k]) | "value \($k)")
' > "$work/findings"

shared="$(jq -r -n --slurpfile c "$work/compose.json" --slurpfile p "$work/portainer.json" '$c[0] | keys - (keys - ($p[0] | keys)) | length')"
while read -r kind key; do
  case "$kind" in
    missing-in-portainer) fail "$key is set for open-webui (compose.openwebui.yaml) but not for rag-openwebui (compose.rag.template.yaml)" ;;
    missing-in-compose) fail "$key is set for rag-openwebui (compose.rag.template.yaml) but not for open-webui (compose.openwebui.yaml); a deployment-specific key goes into only_portainer here" ;;
    stale-only-portainer) fail "$key is listed in only_portainer, but it is not set for rag-openwebui only" ;;
    stale-only-compose) fail "$key is listed in only_compose, but it is not set for open-webui only" ;;
    stale-own-value) fail "$key is listed in own_value, but it is not set in both with different values" ;;
    value) fail "$key differs: open-webui $(jq -c --arg k "$key" '.[$k]' "$work/compose.json"), rag-openwebui $(jq -c --arg k "$key" '.[$k]' "$work/portainer.json")" ;;
  esac
done < "$work/findings"
[ -s "$work/findings" ] || ok "open-webui and rag-openwebui: the same $shared keys with the same values, apart from ${#only_portainer[@]} stack-only keys and ${#own_value[@]} with the deployment's value"

# The OIDC settings: neither file sets them for Open WebUI, and both start it through the wrapper.
for key in "${from_wrapper[@]}"; do
  for file in compose portainer; do
    if jq -e --arg k "$key" 'has($k)' "$work/$file.json" >/dev/null; then fail "$key is set in $file's Open WebUI environment; the start wrapper sets it"; fi
  done
done
docker compose -f "$d/compose.openwebui.yaml" config --format json | jq -c '.services["open-webui"].entrypoint' > "$work/compose.entry"
docker compose -f "$d/portainer/compose.portainer.yaml" --env-file "$d/portainer/stack.env.example" --profile openwebui config --format json \
  | jq -c '.services["rag-openwebui"].entrypoint' > "$work/portainer.entry"
for file in compose portainer; do
  grep -q '"/config/start-with-sign-in.py"' "$work/$file.entry" || fail "$file's Open WebUI does not start through start-with-sign-in.py: $(cat "$work/$file.entry")"
done
ok "neither file passes OIDC settings to Open WebUI (${#from_wrapper[@]} keys); both start it through start-with-sign-in.py"

# The feedback service account: rag-openwebui names it here; init-deployment.sh writes it into Compose's env file.
for key in WEBUI_ADMIN_EMAIL WEBUI_ADMIN_NAME; do
  want="$(sed -n "s/^ *set_env_line \"\$secrets\/openwebui.env\" $key //p" "$d/init-deployment.sh" | tr -d '"')"
  got="$(jq -r --arg k "$key" '.[$k]' "$work/portainer.json")"
  if [ -n "$want" ] && [ "$got" = "$want" ]; then ok "$key: '$got' in both"
  else fail "$key: rag-openwebui '$got', init-deployment.sh's openwebui.env '$want'"; fi
done

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
