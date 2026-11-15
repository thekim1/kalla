#!/usr/bin/env bash
# Host names of the Portainer stack (M-17 setup wizard step S0): Ume.Rag's three names default under UME_DOMAIN, and each
# UME_RAG_*_HOST overrides its own. Offline, with `docker compose config --format json` of compose.portainer.yaml, the
# variables of stack.env.example without its host lines and each case's own, this checks every place a name is used
# (rag-bootstrap's three names, rag-api's Knowledge__PublicBaseUrl, rag-admin's Admin__ApiPublicUrl, rag-openwebui's
# WEBUI_URL and OPENID_REDIRECT_URI) for:
#
#   - only UME_DOMAIN: rag.<domain>, rag-admin.<domain>, chat.<domain>
#   - only UME_RAG_API_HOST: as before the domain (rag-admin's name empty, so rag-bootstrap gives it rag-api's; Open
#     WebUI's empty, its URLs on localhost); never a name ending in a dot
#   - the three UME_RAG_*_HOST, no domain; the domain and one override
#   - neither UME_DOMAIN nor UME_RAG_API_HOST: the stack does not render, and the message names both
#
#   deploy/tests/portainer-hosts.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per case and PASS, or "FAIL: ..." and exits 1.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
# The stack file and the case's variables are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^UME_|^COMPOSE_PROFILES$' || true)
grep -vE '^(UME_DOMAIN|UME_RAG_API_HOST|UME_RAG_ADMIN_HOST|UME_RAG_OPENWEBUI_HOST|COMPOSE_PROFILES)=' \
  "$here/../portainer/stack.env.example" > "$work/base.env"

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }
# render VAR=VALUE...: the stack with the openwebui profile, as JSON in $work/out.json (stderr in $work/err).
render() {
  printf '%s\n' "$@" > "$work/case.env"
  docker compose -f "$stack" --env-file "$work/base.env" --env-file "$work/case.env" --profile openwebui \
    config --format json > "$work/out.json" 2> "$work/err"
}
# expect NAME API ADMIN OPENWEBUI URL-OF-API URL-OF-OPENWEBUI VAR=VALUE...
expect() {
  local name="$1" api="$2" admin="$3" owui="$4" api_url="$5" owui_url="$6"; shift 6
  render "$@" || { fail "$name: the stack does not render: $(cat "$work/err")"; return; }
  local want got
  want="$(jq -nc --arg a "$api" --arg b "$admin" --arg c "$owui" --arg d "$api_url" --arg e "$owui_url" \
    '[$a, $b, $c, $d, $d, $e, "\($e)/oauth/oidc/login/callback"]')"
  got="$(jq -c '.services as $s | [$s["rag-bootstrap"].environment | .UME_RAG_API_HOST, .UME_RAG_ADMIN_HOST,
    .UME_RAG_OPENWEBUI_HOST] + [$s["rag-api"].environment.Knowledge__PublicBaseUrl,
    $s["rag-admin"].environment.Admin__ApiPublicUrl] + [$s["rag-openwebui"].environment | .WEBUI_URL,
    .OPENID_REDIRECT_URI]' "$work/out.json")"
  if [ "$got" = "$want" ]; then ok "$name: $got"; else fail "$name: got $got, expected $want"; fi
}

expect "only UME_DOMAIN" rag.example.se rag-admin.example.se chat.example.se https://rag.example.se https://chat.example.se \
  UME_DOMAIN=example.se
expect "only UME_RAG_API_HOST" rag.kommun.se "" "" https://rag.kommun.se https://localhost \
  UME_RAG_API_HOST=rag.kommun.se
expect "an empty UME_DOMAIN and UME_RAG_API_HOST" rag.kommun.se "" "" https://rag.kommun.se https://localhost \
  UME_DOMAIN= UME_RAG_API_HOST=rag.kommun.se
expect "the three names, no domain" rag.kommun.se admin.kommun.se ai.kommun.se https://rag.kommun.se https://ai.kommun.se \
  UME_RAG_API_HOST=rag.kommun.se UME_RAG_ADMIN_HOST=admin.kommun.se UME_RAG_OPENWEBUI_HOST=ai.kommun.se
expect "the domain and an admin override" rag.example.se admin.kommun.se chat.example.se https://rag.example.se \
  https://chat.example.se UME_DOMAIN=example.se UME_RAG_ADMIN_HOST=admin.kommun.se
expect "the domain and a public URL" rag.example.se rag-admin.example.se chat.example.se https://rag.example.se:8543 \
  https://chat.example.se UME_DOMAIN=example.se UME_RAG_API_PUBLIC_URL=https://rag.example.se:8543

# Unset, and set but empty (Portainer keeps a variable whose value was cleared).
for vars in "" "UME_DOMAIN= UME_RAG_API_HOST="; do
  name="neither UME_DOMAIN nor UME_RAG_API_HOST${vars:+ (both empty)}"
  # shellcheck disable=SC2086 # $vars is a list of VAR=VALUE words
  if render $vars; then
    fail "$name: the stack renders"
  elif grep -q 'Set UME_DOMAIN or UME_RAG_API_HOST' "$work/err"; then
    ok "$name: the stack does not render ($(tr -d '\n' < "$work/err"))"
  else
    fail "$name: the message does not name both: $(cat "$work/err")"
  fi
done

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
