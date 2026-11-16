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
#   - neither UME_DOMAIN nor UME_RAG_API_HOST: rag-bootstrap gets an empty name and its script stops with a message
#     that names both (no ":?" inside a default in the stack file, which some Compose versions evaluate even when the outer variable is set)
#
# The gateway's two names (M-20 S8) the same way, in every place they are used (the gateway bootstrap's and rag-bootstrap's
# UME_GATEWAY_HOST and UME_ADMIN_HOST, adminapi's Gateway__BaseUrl, rag-admin's Admin__GatewayAdminUrl): only UME_DOMAIN
# (llm.<domain>, llm-admin.<domain>); UME_GATEWAY_HOST or UME_ADMIN_HOST overriding its own; UME_GATEWAY_PUBLIC_URL and
# UME_ADMIN_PUBLIC_URL with a port; both names and no domain; only UME_GATEWAY_HOST and no domain, where rag-admin's
# Admin__GatewayAdminUrl is printed as a note, not checked (it has no host name to give).
#
# And stack.env.example as M-20's done-when has it: exactly one "# CHANGE" line, for UME_DOMAIN, and no sign-in value or
# secret set.
#
#   deploy/tests/portainer-hosts.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per case and PASS, or "FAIL: ..." and exits 1.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
example="$here/../portainer/stack.env.example"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
# The stack file and the case's variables are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^UME_|^COMPOSE_PROFILES$' || true)
grep -vE '^(UME_DOMAIN|UME_RAG_API_HOST|UME_RAG_ADMIN_HOST|UME_RAG_OPENWEBUI_HOST|UME_GATEWAY_HOST|UME_GATEWAY_PUBLIC_URL|UME_ADMIN_HOST|UME_ADMIN_PUBLIC_URL|COMPOSE_PROFILES)=' \
  "$example" > "$work/base.env"

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }

# The example's one typed value: a single CHANGE line, and the first variable after it is UME_DOMAIN.
changes="$(grep -c '^# CHANGE' "$example" || true)"
after="$(awk '/^# CHANGE/ { found = 1; next } found && !/^#/ { print; exit }' "$example")"
if [ "$changes" = 1 ] && [ "${after%%=*}" = UME_DOMAIN ]; then ok "stack.env.example: one CHANGE line, for UME_DOMAIN"
else fail "stack.env.example has $changes CHANGE line(s), the first variable after one is '${after%%=*}'; expected 1, UME_DOMAIN"; fi
set_values="$(grep -E '^(UME_OIDC_[A-Z_]*|UME_RAG_OIDC_[A-Z_]*|UME_RAG_OPENWEBUI_OIDC_[A-Z_]*|[A-Z0-9_]*(SECRET|PASSWORD|_KEY)[A-Z0-9_]*)=.' "$example" || true)"
if [ -z "$set_values" ]; then ok "stack.env.example sets no sign-in value and no secret"
else fail "stack.env.example sets: $(cut -d= -f1 <<< "$set_values" | tr '\n' ' ')"; fi
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

# gateway_values: the gateway's names and addresses from $work/out.json, as a JSON array: the gateway bootstrap's
# UME_GATEWAY_HOST and UME_ADMIN_HOST, rag-bootstrap's two, adminapi's Gateway__BaseUrl, rag-admin's Admin__GatewayAdminUrl.
gateway_values() {
  jq -c '.services as $s | [$s.bootstrap.environment | .UME_GATEWAY_HOST, .UME_ADMIN_HOST]
    + [$s["rag-bootstrap"].environment | .UME_GATEWAY_HOST, .UME_ADMIN_HOST]
    + [$s.adminapi.environment.Gateway__BaseUrl, $s["rag-admin"].environment.Admin__GatewayAdminUrl]' "$work/out.json"
}
# expect_gateway NAME GATEWAY ADMIN BASE-URL ADMIN-URL VAR=VALUE...: both bootstraps get the same two names.
expect_gateway() {
  local name="$1" gw="$2" admin="$3" base="$4" admin_url="$5"; shift 5
  render "$@" || { fail "$name: the stack does not render: $(cat "$work/err")"; return; }
  local want got
  want="$(jq -nc --arg a "$gw" --arg b "$admin" --arg c "$base" --arg d "$admin_url" '[$a, $b, $a, $b, $c, $d]')"
  got="$(gateway_values)"
  if [ "$got" = "$want" ]; then ok "$name: $got"; else fail "$name: got $got, expected $want"; fi
}

expect_gateway "the gateway, only UME_DOMAIN" llm.example.se llm-admin.example.se https://llm.example.se https://llm-admin.example.se \
  UME_DOMAIN=example.se
expect_gateway "the gateway, the domain and UME_GATEWAY_HOST" ai.kommun.se llm-admin.example.se https://ai.kommun.se \
  https://llm-admin.example.se UME_DOMAIN=example.se UME_GATEWAY_HOST=ai.kommun.se
expect_gateway "the gateway, the domain and UME_ADMIN_HOST" llm.example.se ai-admin.kommun.se https://llm.example.se \
  https://ai-admin.kommun.se UME_DOMAIN=example.se UME_ADMIN_HOST=ai-admin.kommun.se
expect_gateway "the gateway, the domain and both public URLs with a port" llm.example.se llm-admin.example.se \
  https://llm.example.se:8443 https://llm-admin.example.se:9443 \
  UME_DOMAIN=example.se UME_GATEWAY_PUBLIC_URL=https://llm.example.se:8443 UME_ADMIN_PUBLIC_URL=https://llm-admin.example.se:9443
expect_gateway "the gateway, both names and no domain" llm.kommun.se llm-admin.kommun.se https://llm.kommun.se https://llm-admin.kommun.se \
  UME_GATEWAY_HOST=llm.kommun.se UME_ADMIN_HOST=llm-admin.kommun.se UME_RAG_API_HOST=rag.kommun.se
# Only the gateway's name and no domain, as before UME_DOMAIN: the admin's name stays empty, which the gateway's bootstrap
# reads as the gateway's name (its certificate) and rag-bootstrap's proxy as "no block of its own". rag-admin's
# Admin__GatewayAdminUrl has no name to build an address from; what it gets is shown, not judged here.
name="the gateway, only UME_GATEWAY_HOST and no domain"
if render UME_GATEWAY_HOST=llm.kommun.se UME_RAG_API_HOST=rag.kommun.se; then
  got="$(gateway_values)"
  want='["llm.kommun.se","","llm.kommun.se","","https://llm.kommun.se"]'
  if [ "$(jq -c '.[:5]' <<< "$got")" = "$want" ]; then
    ok "$name: $(jq -c '.[:5]' <<< "$got")"
    echo "note: $name: rag-admin's Admin__GatewayAdminUrl is '$(jq -r '.[5]' <<< "$got")'"
  else
    fail "$name: got $(jq -c '.[:5]' <<< "$got"), expected $want"
  fi
else
  fail "$name: the stack does not render: $(cat "$work/err")"
fi

# Unset, and set but empty (Portainer keeps a variable whose value was cleared): the stack renders with an empty name,
# and rag-bootstrap, which every service waits for, stops on it with a message that names both. The stack file has no
# ":?" inside a default: some Compose versions evaluate it even when the outer variable is set (5.0.2 does not), which refuses a stack with
# only UME_RAG_API_HOST (seen on GitHub's runner with the gateway's file, thekim1/llmgateway#102).
grep -q ':?[^}]*}}' "$stack" && fail "the stack file has a required-variable form inside a default"
grep -q 'UME_RAG_API_HOST:?Set UME_DOMAIN or UME_RAG_API_HOST' "$stack" \
  || fail "rag-bootstrap's script does not stop on an empty UME_RAG_API_HOST with a message that names both"
for vars in "" "UME_DOMAIN= UME_RAG_API_HOST="; do
  name="neither UME_DOMAIN nor UME_RAG_API_HOST${vars:+ (both empty)}"
  # shellcheck disable=SC2086 # $vars is a list of VAR=VALUE words
  if ! render $vars; then
    fail "$name: the stack does not render: $(cat "$work/err")"
  elif [ "$(jq -r '.services["rag-bootstrap"].environment.UME_RAG_API_HOST' "$work/out.json")" = "" ]; then
    ok "$name: rag-bootstrap gets an empty UME_RAG_API_HOST and stops on it"
  else
    fail "$name: rag-bootstrap's UME_RAG_API_HOST is not empty"
  fi
done

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
