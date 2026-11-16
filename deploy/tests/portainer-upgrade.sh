#!/usr/bin/env bash
# An existing deployment's variables with the current Portainer stack file (docs/portainer.md, "Upgrade from 0.6.0"):
# the upgrade is "paste the new file, keep your variables", so the new file must render with the previous release's
# variables and give each service what the upgrade steps promise. Offline, with `docker compose config --format json`
# of compose.portainer.yaml and fixtures/stack-0.6.0.env (v0.6.0's stack.env.example with its two secrets filled in):
#
#   - the stack renders with no new variable (no UME_DOMAIN: the host names are the stack's own);
#   - the sign-in stays declared: rag-admin gets the authority and rag-bootstrap the client secret, so rag-admin counts
#     setup as done at its first start and never opens the wizard; Setup__Reopen is empty;
#   - the token-shape values are passed empty (the code's defaults apply, as the old stack file's defaults did);
#   - Open WebUI's client and secret still reach rag-api and rag-admin (its start wrapper asks rag-api for them);
#   - the three host names and the public URLs are unchanged;
#   - the gateway is unchanged by M-20 S8 (one typed value): its bootstrap gets the stack's own UME_INTEGRATION_MANIFEST
#     (with its owner), not the stack file's default; adminapi keeps its authority and Gateway__BaseUrl; rag-bootstrap's
#     proxy gets the gateway's two names, and rag-admin's Admin__GatewayAdminUrl is the gateway admin's name.
#
# The manifest's default (the stack file's, deploy/portainer/integration-manifest.json) applies only while the variable
# is unset: the 0.6.0 variables without their manifest line get it (compared as JSON), and with UME_INTEGRATION_MANIFEST=
# (set, empty) the gateway's bootstrap gets an empty value and provisions nothing.
#
# And fixtures/stack-stage1.env, the variables of stack.env.example before M-20 S8 (five CHANGE values, among them
# UME_DOMAIN, and the manifest with an owner): the stack's own authority, gateway names, public URL and manifest win over
# UME_DOMAIN's defaults, and Ume.Rag's names come from the domain.
#
#   deploy/tests/portainer-upgrade.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per check and PASS, or "FAIL: ..." and exits 1. It reads the
# files only; the boot of an upgraded stack is not tested here.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
fixture="$here/fixtures/stack-0.6.0.env"
default_manifest="$here/../portainer/integration-manifest.json"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
# The stack file and the fixture's variables are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^UME_|^COMPOSE_PROFILES$' || true)

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }

# render ENV-FILE WHAT: the stack with these variables as JSON in $work/out.json; exits when it does not render.
render() {
  if ! docker compose -f "$stack" --env-file "$1" config --format json > "$work/out.json" 2> "$work/err"; then
    echo "FAIL: the stack does not render with $2: $(cat "$work/err")"; exit 1
  fi
}
# is SERVICE VARIABLE EXPECTED WHAT
is() {
  local got; got="$(jq -r --arg s "$1" --arg v "$2" '.services[$s].environment[$v] // "<absent>"' "$work/out.json")"
  if [ "$got" = "$3" ]; then ok "$4"; else fail "$4: $1's $2 is '$got', expected '$3'"; fi
}
# manifest: the gateway bootstrap's UME_INTEGRATION_MANIFEST as rendered.
manifest() { jq -r '.services.bootstrap.environment.UME_INTEGRATION_MANIFEST // "<absent>"' "$work/out.json"; }
# fixture_value FILE NAME: the value of NAME= in a variables file.
fixture_value() { sed -n "s/^$2=//p" "$1"; }

render "$fixture" "0.6.0's variables"
ok "the stack renders with 0.6.0's variables and nothing added"
authority=https://keycloak.example.se/realms/ume
is rag-admin Oidc__Authority "$authority" "rag-admin's authority is the stack's (declared)"
is rag-bootstrap UME_RAG_OIDC_CLIENT_SECRET a-client-secret "rag-bootstrap gets rag-admin's client secret to write as a file (declared)"
is rag-admin Setup__Reopen "" "nothing re-opens the wizard"
for key in Oidc__ClientId Oidc__RoleClaim Oidc__DepartmentClaim Oidc__TeamClaim Oidc__GroupClaim Oidc__ExtraScopes__0 Oidc__TenantId; do
  is rag-admin "$key" "" "rag-admin's $key is passed empty (the code's default applies)"
done
is rag-api Oidc__Authority "$authority" "rag-api's authority is the stack's"
is rag-api OpenWebUi__Oidc__ClientId open-webui "Open WebUI's client id reaches rag-api"
is rag-admin OpenWebUi__Oidc__ClientId open-webui "Open WebUI's client id reaches rag-admin"
is rag-bootstrap UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET a-client-secret "rag-bootstrap gets Open WebUI's client secret to write as files"
is rag-bootstrap UME_RAG_API_HOST rag.example.se "rag-api's host name is unchanged"
is rag-bootstrap UME_RAG_ADMIN_HOST rag-admin.example.se "rag-admin's host name is unchanged"
is rag-bootstrap UME_RAG_OPENWEBUI_HOST chat.example.se "Open WebUI's host name is unchanged"
is rag-api Knowledge__PublicBaseUrl https://rag.example.se "rag-api's public URL is unchanged"
is rag-openwebui WEBUI_URL https://chat.example.se "Open WebUI's address is unchanged"
is bootstrap UME_GATEWAY_HOST llm.example.se "the gateway's host name is unchanged"
is bootstrap UME_ADMIN_HOST llm-admin.example.se "the gateway admin's host name is unchanged"
is adminapi Oidc__Authority "$authority" "the gateway's admin keeps its authority"
is adminapi Gateway__BaseUrl https://llm.example.se "the gateway's public URL is unchanged (UME_GATEWAY_PUBLIC_URL)"
is rag-bootstrap UME_GATEWAY_HOST llm.example.se "rag-bootstrap's proxy gets the gateway's host name"
is rag-bootstrap UME_ADMIN_HOST llm-admin.example.se "rag-bootstrap's proxy gets the gateway admin's host name"
is rag-admin Admin__GatewayAdminUrl https://llm-admin.example.se "rag-admin's links to the gateway admin use its host name"
own="$(fixture_value "$fixture" UME_INTEGRATION_MANIFEST)"
[ -n "$own" ] || fail "fixtures/stack-0.6.0.env has no UME_INTEGRATION_MANIFEST"
if [ "$(manifest)" = "$own" ] && [ "$(jq -r '.integration.owner.teamCode' <<< "$own")" = ume-rag ]; then
  ok "the gateway's bootstrap gets the stack's own manifest, with its owner, not the stack file's default"
else
  fail "the gateway's bootstrap's UME_INTEGRATION_MANIFEST is not the stack's own: $(manifest)"
fi

# The manifest's default: the 0.6.0 variables without their manifest line, then with it set and empty.
grep -v '^UME_INTEGRATION_MANIFEST=' "$fixture" > "$work/no-manifest.env"
render "$work/no-manifest.env" "0.6.0's variables without UME_INTEGRATION_MANIFEST"
if jq -e . >/dev/null 2>&1 <<< "$(manifest)" && [ "$(jq -S -c . <<< "$(manifest)")" = "$(jq -S -c . "$default_manifest")" ]; then
  ok "without UME_INTEGRATION_MANIFEST the gateway's bootstrap gets the default manifest (integration-manifest.json)"
else
  fail "without UME_INTEGRATION_MANIFEST the gateway's bootstrap gets '$(manifest)', not integration-manifest.json"
fi
{ cat "$work/no-manifest.env"; echo "UME_INTEGRATION_MANIFEST="; } > "$work/empty-manifest.env"
render "$work/empty-manifest.env" "0.6.0's variables and an empty UME_INTEGRATION_MANIFEST"
if [ "$(manifest)" = "" ]; then ok "an empty UME_INTEGRATION_MANIFEST stays empty: nothing is provisioned"
else fail "an empty UME_INTEGRATION_MANIFEST gives the gateway's bootstrap '$(manifest)', expected empty"; fi

# A stack set up before M-20 S8 (stage 1): its own values beside UME_DOMAIN.
stage1="$here/fixtures/stack-stage1.env"
render "$stage1" "stage 1's variables"
ok "the stack renders with stage 1's variables"
authority=https://login.kommun.se/realms/ume
is adminapi Oidc__Authority "$authority" "stage 1: the gateway's admin keeps the stack's authority"
is rag-admin Oidc__Authority "$authority" "stage 1: rag-admin's authority is the stack's"
is adminapi Gateway__BaseUrl https://ai.kommun.se "stage 1: the gateway's public URL is the stack's, not llm.<domain>"
is bootstrap UME_GATEWAY_HOST ai.kommun.se "stage 1: the gateway's host name is the stack's"
is bootstrap UME_ADMIN_HOST ai-admin.kommun.se "stage 1: the gateway admin's host name is the stack's"
is rag-bootstrap UME_GATEWAY_HOST ai.kommun.se "stage 1: rag-bootstrap's proxy gets the stack's gateway name"
is rag-bootstrap UME_ADMIN_HOST ai-admin.kommun.se "stage 1: rag-bootstrap's proxy gets the stack's gateway admin name"
is rag-admin Admin__GatewayAdminUrl https://ai-admin.kommun.se "stage 1: rag-admin's links to the gateway admin use the stack's name"
is rag-bootstrap UME_RAG_API_HOST rag.kommun.se "stage 1: rag-api's host name is under UME_DOMAIN"
is rag-bootstrap UME_RAG_ADMIN_HOST rag-admin.kommun.se "stage 1: rag-admin's host name is under UME_DOMAIN"
if [ "$(manifest)" = "$(fixture_value "$stage1" UME_INTEGRATION_MANIFEST)" ] && [ "$(jq -r '.integration.owner.teamCode' <<< "$(manifest)")" = ume-rag ]; then
  ok "stage 1: the gateway's bootstrap gets the stack's own manifest, with its owner"
else
  fail "stage 1: the gateway's bootstrap's UME_INTEGRATION_MANIFEST is not the stack's own: $(manifest)"
fi

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
