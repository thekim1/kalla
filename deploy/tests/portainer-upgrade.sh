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
#   - the three host names and the public URLs are unchanged.
#
#   deploy/tests/portainer-upgrade.sh [STACK.yaml]   [deploy/portainer/compose.portainer.yaml]
#
# Needs Docker with Compose v2 and jq. Prints "ok: ..." per check and PASS, or "FAIL: ..." and exits 1. It reads the
# files only; the boot of an upgraded stack is not tested here.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
stack="${1:-$here/../portainer/compose.portainer.yaml}"
for tool in docker jq; do command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }; done
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
# The stack file and the fixture's variables are under test: an inherited variable would override them.
while IFS= read -r var; do unset "$var"; done < <(compgen -e | grep -E '^UME_|^COMPOSE_PROFILES$' || true)

failures=0
ok() { echo "ok: $*"; }
fail() { echo "FAIL: $*"; failures=$((failures + 1)); }

if ! docker compose -f "$stack" --env-file "$here/fixtures/stack-0.6.0.env" config --format json > "$work/out.json" 2> "$work/err"; then
  echo "FAIL: the stack does not render with 0.6.0's variables: $(cat "$work/err")"; exit 1
fi
ok "the stack renders with 0.6.0's variables and nothing added"

# is SERVICE VARIABLE EXPECTED WHAT
is() {
  local got; got="$(jq -r --arg s "$1" --arg v "$2" '.services[$s].environment[$v] // "<absent>"' "$work/out.json")"
  if [ "$got" = "$3" ]; then ok "$4"; else fail "$4: $1's $2 is '$got', expected '$3'"; fi
}
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
is adminapi Oidc__Authority "$authority" "the gateway's admin keeps its authority"

if [ "$failures" -gt 0 ]; then echo "FAIL: $failures check(s) failed"; exit 1; fi
echo PASS
