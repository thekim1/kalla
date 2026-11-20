#!/usr/bin/env bash
# An existing deployment's variables with the current Portainer stack file (docs/portainer.md, "Upgrade"):
# the upgrade is "paste the new file, keep your variables", so the new file must render with the previous release's
# variables and give each service what the upgrade steps promise. Offline, with `docker compose config --format json`
# of compose.portainer.yaml and fixtures/stack-0.6.0.env (v0.6.0's stack.env.example with its two secrets filled in):
#
#   - the stack renders with no new variable (no FORS_DOMAIN: the host names are the stack's own);
#   - the sign-in stays declared: kalla-admin gets the authority and kalla-bootstrap the client secret, so kalla-admin counts
#     setup as done at its first start and never opens the wizard; Setup__Reopen is empty;
#   - the token-shape values are passed empty (the code's defaults apply, as the old stack file's defaults did);
#   - Open WebUI's client and secret still reach kalla-api and kalla-admin (its start wrapper asks kalla-api for them);
#   - the three host names and the public URLs are unchanged;
#   - the gateway is unchanged by M-20 S8 (one typed value): its bootstrap gets the stack's own FORS_INTEGRATION_MANIFEST
#     (with its owner), not the stack file's default; adminapi keeps its authority and Gateway__BaseUrl; kalla-bootstrap's
#     proxy gets the gateway's two names, and kalla-admin's Admin__GatewayAdminUrl is the gateway admin's name.
#
# The manifest's default (the stack file's, deploy/portainer/integration-manifest.json) applies only while the variable
# is unset: the 0.6.0 variables without their manifest line get it (compared as JSON), and with FORS_INTEGRATION_MANIFEST=
# (set, empty) the gateway's bootstrap gets an empty value and provisions nothing.
#
# fixtures/stack-0.5.1.env, a 0.5.x stack's variables (keys issued by hand, retired variables still there): it renders
# too, stays declared, hands a hand-issued key to kalla-bootstrap and needs no FORS_INTEGRATION_API_URL.
#
# And fixtures/stack-stage1.env, the variables of stack.env.example before M-20 S8 (five CHANGE values, among them
# FORS_DOMAIN, and the manifest with an owner): the stack's own authority, gateway names, public URL and manifest win over
# FORS_DOMAIN's defaults, and Källa's names come from the domain.
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
# manifest: the gateway bootstrap's FORS_INTEGRATION_MANIFEST as rendered.
manifest() { jq -r '.services.bootstrap.environment.FORS_INTEGRATION_MANIFEST // "<absent>"' "$work/out.json"; }
# fixture_value FILE NAME: the value of NAME= in a variables file.
fixture_value() { sed -n "s/^$2=//p" "$1"; }

render "$fixture" "0.6.0's variables"
ok "the stack renders with 0.6.0's variables and nothing added"
authority=https://keycloak.example.se/realms/fors
is kalla-admin Oidc__Authority "$authority" "kalla-admin's authority is the stack's (declared)"
is kalla-bootstrap KALLA_OIDC_CLIENT_SECRET a-client-secret "kalla-bootstrap gets kalla-admin's client secret to write as a file (declared)"
is kalla-admin Setup__Reopen "" "nothing re-opens the wizard"
for key in Oidc__ClientId Oidc__RoleClaim Oidc__DepartmentClaim Oidc__TeamClaim Oidc__GroupClaim Oidc__ExtraScopes__0 Oidc__TenantId; do
  is kalla-admin "$key" "" "kalla-admin's $key is passed empty (the code's default applies)"
done
is kalla-api Oidc__Authority "$authority" "kalla-api's authority is the stack's"
is kalla-api OpenWebUi__Oidc__ClientId open-webui "Open WebUI's client id reaches kalla-api"
is kalla-admin OpenWebUi__Oidc__ClientId open-webui "Open WebUI's client id reaches kalla-admin"
is kalla-bootstrap KALLA_OPENWEBUI_OIDC_CLIENT_SECRET a-client-secret "kalla-bootstrap gets Open WebUI's client secret to write as files"
is kalla-bootstrap KALLA_API_HOST rag.example.se "kalla-api's host name is unchanged"
is kalla-bootstrap KALLA_ADMIN_HOST kalla-admin.example.se "kalla-admin's host name is unchanged"
is kalla-bootstrap KALLA_OPENWEBUI_HOST chat.example.se "Open WebUI's host name is unchanged"
is kalla-api Knowledge__PublicBaseUrl https://rag.example.se "kalla-api's public URL is unchanged"
is kalla-openwebui WEBUI_URL https://chat.example.se "Open WebUI's address is unchanged"
is bootstrap FORS_GATEWAY_HOST llm.example.se "the gateway's host name is unchanged"
is bootstrap FORS_ADMIN_HOST llm-admin.example.se "the gateway admin's host name is unchanged"
is adminapi Oidc__Authority "$authority" "the gateway's admin keeps its authority"
is adminapi Gateway__BaseUrl https://llm.example.se "the gateway's public URL is unchanged (FORS_GATEWAY_PUBLIC_URL)"
is kalla-bootstrap FORS_GATEWAY_HOST llm.example.se "kalla-bootstrap's proxy gets the gateway's host name"
is kalla-bootstrap FORS_ADMIN_HOST llm-admin.example.se "kalla-bootstrap's proxy gets the gateway admin's host name"
is kalla-admin Admin__GatewayAdminUrl https://llm-admin.example.se "kalla-admin's links to the gateway admin use its host name"
own="$(fixture_value "$fixture" FORS_INTEGRATION_MANIFEST)"
[ -n "$own" ] || fail "fixtures/stack-0.6.0.env has no FORS_INTEGRATION_MANIFEST"
if [ "$(manifest)" = "$own" ] && [ "$(jq -r '.integration.owner.teamCode' <<< "$own")" = kalla ]; then
  ok "the gateway's bootstrap gets the stack's own manifest, with its owner, not the stack file's default"
else
  fail "the gateway's bootstrap's FORS_INTEGRATION_MANIFEST is not the stack's own: $(manifest)"
fi

# The manifest's default: the 0.6.0 variables without their manifest line, then with it set and empty.
grep -v '^FORS_INTEGRATION_MANIFEST=' "$fixture" > "$work/no-manifest.env"
render "$work/no-manifest.env" "0.6.0's variables without FORS_INTEGRATION_MANIFEST"
if jq -e . >/dev/null 2>&1 <<< "$(manifest)" && [ "$(jq -S -c . <<< "$(manifest)")" = "$(jq -S -c . "$default_manifest")" ]; then
  ok "without FORS_INTEGRATION_MANIFEST the gateway's bootstrap gets the default manifest (integration-manifest.json)"
else
  fail "without FORS_INTEGRATION_MANIFEST the gateway's bootstrap gets '$(manifest)', not integration-manifest.json"
fi
{ cat "$work/no-manifest.env"; echo "FORS_INTEGRATION_MANIFEST="; } > "$work/empty-manifest.env"
render "$work/empty-manifest.env" "0.6.0's variables and an empty FORS_INTEGRATION_MANIFEST"
if [ "$(manifest)" = "" ]; then ok "an empty FORS_INTEGRATION_MANIFEST stays empty: nothing is provisioned"
else fail "an empty FORS_INTEGRATION_MANIFEST gives the gateway's bootstrap '$(manifest)', expected empty"; fi

# A stack set up before M-20 S8 (stage 1): its own values beside FORS_DOMAIN.
stage1="$here/fixtures/stack-stage1.env"
render "$stage1" "stage 1's variables"
ok "the stack renders with stage 1's variables"
authority=https://login.kommun.se/realms/fors
is adminapi Oidc__Authority "$authority" "stage 1: the gateway's admin keeps the stack's authority"
is kalla-admin Oidc__Authority "$authority" "stage 1: kalla-admin's authority is the stack's"
is adminapi Gateway__BaseUrl https://ai.kommun.se "stage 1: the gateway's public URL is the stack's, not llm.<domain>"
is bootstrap FORS_GATEWAY_HOST ai.kommun.se "stage 1: the gateway's host name is the stack's"
is bootstrap FORS_ADMIN_HOST ai-admin.kommun.se "stage 1: the gateway admin's host name is the stack's"
is kalla-bootstrap FORS_GATEWAY_HOST ai.kommun.se "stage 1: kalla-bootstrap's proxy gets the stack's gateway name"
is kalla-bootstrap FORS_ADMIN_HOST ai-admin.kommun.se "stage 1: kalla-bootstrap's proxy gets the stack's gateway admin name"
is kalla-admin Admin__GatewayAdminUrl https://ai-admin.kommun.se "stage 1: kalla-admin's links to the gateway admin use the stack's name"
is kalla-bootstrap KALLA_API_HOST rag.kommun.se "stage 1: kalla-api's host name is under FORS_DOMAIN"
is kalla-bootstrap KALLA_ADMIN_HOST kalla-admin.kommun.se "stage 1: kalla-admin's host name is under FORS_DOMAIN"
if [ "$(manifest)" = "$(fixture_value "$stage1" FORS_INTEGRATION_MANIFEST)" ] && [ "$(jq -r '.integration.owner.teamCode' <<< "$(manifest)")" = kalla ]; then
  ok "stage 1: the gateway's bootstrap gets the stack's own manifest, with its owner"
else
  fail "stage 1: the gateway's bootstrap's FORS_INTEGRATION_MANIFEST is not the stack's own: $(manifest)"
fi

# A 0.5.x stack (docs/portainer.md, "Upgrade": one step from 0.5.x): its variables, with keys issued by hand.
old5="$here/fixtures/stack-0.5.1.env"
sed -e 's/^KALLA_API_GATEWAY_KEY=$/KALLA_API_GATEWAY_KEY=hand-issued-api-key/' "$old5" > "$work/0.5.env"
render "$work/0.5.env" "0.5.x's variables"
ok "the stack renders with 0.5.x's variables and nothing added (retired ones are ignored)"
authority=https://keycloak.example.se/realms/fors
is kalla-admin Oidc__Authority "$authority" "0.5.x: kalla-admin's authority is the stack's (declared, no setup wizard)"
is kalla-bootstrap KALLA_OIDC_CLIENT_SECRET a-value "0.5.x: kalla-bootstrap gets kalla-admin's client secret (declared)"
is adminapi Oidc__Authority "$authority" "0.5.x: the gateway's admin keeps its authority"
is kalla-bootstrap KALLA_API_GATEWAY_KEY hand-issued-api-key "0.5.x: a key issued by hand reaches kalla-bootstrap, where it wins over the manifest's"
is kalla-api Knowledge__PublicBaseUrl https://rag.example.se "0.5.x: kalla-api's public URL is the stack's KALLA_API_PUBLIC_URL"
is kalla-bootstrap KALLA_API_HOST rag.example.se "0.5.x: kalla-api's host name is unchanged"
is kalla-bootstrap KALLA_OPENWEBUI_HOST chat.example.se "0.5.x: Open WebUI's host name is unchanged"
is gateway Gateway__IntegrationApiUrl https://adminapi:8443/integration/v1 "0.5.x: no FORS_INTEGRATION_API_URL is needed (the gateway announces adminapi's address in the stack by default)"
if [ "$(jq -S -c . <<< "$(manifest)" 2>/dev/null)" = "$(jq -S -c . "$default_manifest")" ]; then
  ok "0.5.x: without FORS_INTEGRATION_MANIFEST the gateway's bootstrap gets the default manifest"
else
  fail "0.5.x: the gateway's bootstrap gets '$(manifest)', not integration-manifest.json"
fi

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
