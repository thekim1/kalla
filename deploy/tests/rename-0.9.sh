#!/usr/bin/env bash
# The variables of a Portainer stack from before 0.9.0, renamed the way 0.9.0's upgrade note (and Fors 0.12's guide,
# "Portainer") tells an operator to, for the upgrade tests (portainer-upgrade.sh, portainer-upgrade-boot.sh):
#
#   - UME_RAG_* becomes KALLA_*, RAG_*_IMAGE becomes KALLA_*_IMAGE, then UME_* becomes FORS_* (in that order, so that
#     none becomes FORS_RAG_*), as deploy/upgrade/upgrade-to-0.9.sh does to a Compose .env;
#   - the old defaults a stack relied on are written as values: KALLA_OIDC_CLIENT_ID=ume-rag-admin when Källa's sign-in
#     is declared (an authority and KALLA_OIDC_CLIENT_SECRET) and names no client; Fors's FORS_OIDC_CLIENT_ID=ume-admin
#     when FORS_OIDC_AUTHORITY is set and names no client, and FORS_DATA_AUDIENCE=ume-data-api when unset.
#
#   deploy/tests/rename-0.9.sh OLD.env NEW.env
set -euo pipefail

in="${1:?usage: rename-0.9.sh OLD.env NEW.env}"; out="${2:?usage: rename-0.9.sh OLD.env NEW.env}"
sed -E -e 's/(^|[^A-Za-z0-9_])UME_RAG_/\1KALLA_/g' -e 's/(^|[^A-Za-z0-9_])RAG_([A-Z]+)_IMAGE/\1KALLA_\2_IMAGE/g' \
  -e 's/(^|[^A-Za-z0-9_])UME_/\1FORS_/g' "$in" > "$out"
# A last line without its newline would run into the first line added below.
[ ! -s "$out" ] || [ -z "$(tail -c 1 "$out")" ] || echo >> "$out"

has() { grep -qE "^$1=" "$out"; }       # set, also empty
set_to() { grep -qE "^$1=." "$out"; }   # set to a value
if { set_to KALLA_OIDC_AUTHORITY || set_to FORS_OIDC_AUTHORITY; } && set_to KALLA_OIDC_CLIENT_SECRET && ! has KALLA_OIDC_CLIENT_ID; then
  echo KALLA_OIDC_CLIENT_ID=ume-rag-admin >> "$out"
fi
if set_to FORS_OIDC_AUTHORITY && ! has FORS_OIDC_CLIENT_ID; then echo FORS_OIDC_CLIENT_ID=ume-admin >> "$out"; fi
has FORS_DATA_AUDIENCE || echo FORS_DATA_AUDIENCE=ume-data-api >> "$out"
