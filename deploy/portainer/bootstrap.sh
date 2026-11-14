#!/bin/sh
# One-shot bootstrap of the RAG part of compose.portainer.yaml (the gateway's stack plus RAG, one Portainer stack).
# The Portainer counterpart of deploy/init-deployment.sh, after the gateway's deploy/portainer/bootstrap.sh: creates
# RAG's internal CA, its service certificates, the Data Protection certificate, the database passwords, the key pepper,
# the api's keys, the worker's crawl key pepper, the renderer's WebSocket path and the blob master key in the rag-certs
# and rag-secrets volumes, and copies the static configuration into rag-config. Idempotent: existing secrets are never
# overwritten (a new pepper, key or Data Protection certificate would break existing data); service certificates are
# re-issued when they expire within 30 days or their host names change. Files that follow a variable (the gateway
# credential keys, the OIDC client secret) are written again on every start, and removed when the variable is empty.
#
# Trust: the services that call out (rag-api, rag-admin, rag-worker) trust RAG's CA plus the gateway's (its ume-certs
# volume, mounted at /gateway-certs), so https://gateway:8443 verifies.
set -eu
umask 077
C=/certs
S=/secrets
F=/conf
: "${UME_RAG_API_HOST:?Set UME_RAG_API_HOST (public hostname of rag-api)}"
ADMIN_HOST="${UME_RAG_ADMIN_HOST:-$UME_RAG_API_HOST}"
for d in postgres api admin worker parser migrations; do mkdir -p "$C/$d"; done
for d in postgres api admin worker migrations keys renderer openwebui; do mkdir -p "$S/$d"; done
rand() { openssl rand -hex 32; }
quiet() { "$@" >/dev/null 2>&1; }

if [ ! -f "$C/ca.pem" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout "$C/ca.key" -out "$C/ca.pem" -days 3650 -subj /CN=ume-rag-internal-ca \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
fi

# issue DIR SAN-LIST: (re)issue unless a valid certificate for exactly these names, from the current CA, exists
issue() {
  svc="$1"; san="$2"; dir="$C/$svc"
  if [ -f "$dir/$svc.crt" ] && [ "$(cat "$dir/.san" 2>/dev/null)" = "$san" ] &&
     quiet openssl x509 -checkend 2592000 -noout -in "$dir/$svc.crt" && quiet openssl verify -CAfile "$C/ca.pem" "$dir/$svc.crt"; then
    return
  fi
  echo "rag-bootstrap: issuing certificate for $svc ($san)"
  quiet openssl req -newkey rsa:3072 -nodes -keyout "$dir/$svc.key" -out "/tmp/$svc.csr" -subj "/CN=$svc"
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n' \
    "$san" > "/tmp/$svc.ext"
  quiet openssl x509 -req -in "/tmp/$svc.csr" -CA "$C/ca.pem" -CAkey "$C/ca.key" -CAcreateserial -CAserial /tmp/ca.srl \
    -out "$dir/$svc.crt" -days 825 -extfile "/tmp/$svc.ext"
  printf '%s' "$san" > "$dir/.san"
}
issue postgres "DNS:rag-postgres,DNS:localhost"
issue api "DNS:rag-api,DNS:localhost,DNS:$UME_RAG_API_HOST"
issue admin "DNS:rag-admin,DNS:localhost,DNS:$ADMIN_HOST"
issue worker "DNS:rag-worker,DNS:localhost"
issue parser "DNS:rag-parser,DNS:localhost"

if [ ! -f "$C/data-protection.pfx" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout /tmp/dp.key -out /tmp/dp.crt -days 3650 -subj /CN=ume-rag-data-protection
  quiet openssl pkcs12 -export -inkey /tmp/dp.key -in /tmp/dp.crt -out "$C/data-protection.pfx" -passout pass:
fi
for d in api admin migrations; do cp "$C/data-protection.pfx" "$C/$d/data-protection.pfx"; done
for d in postgres parser migrations; do cp "$C/ca.pem" "$C/$d/ca.pem"; done
[ -f /gateway-certs/ca.pem ] || { echo "rag-bootstrap: the gateway's CA (/gateway-certs/ca.pem) is missing" >&2; exit 1; }
for d in api admin worker; do cat "$C/ca.pem" /gateway-certs/ca.pem > "$C/$d/ca.pem"; done

# --- Database (connection pools per instance, data-model §1.1: api 12, admin 5, worker 10, migrator 2) ---------------
for role in bootstrap migrator api admin worker; do
  [ -f "$S/postgres/${role}_password" ] || rand > "$S/postgres/${role}_password"
done
connection() {
  printf 'Host=rag-postgres;Database=ragdb;Username=ume_rag_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable;Maximum Pool Size=%s' \
    "$1" "$(tr -d '\r\n' < "$S/postgres/${1}_password")" "$2"
}
connection migrator 2 > "$S/migrations/ConnectionStrings__ragdb"
connection api 12 > "$S/api/ConnectionStrings__ragdb"
connection admin 5 > "$S/admin/ConnectionStrings__ragdb"
connection worker 10 > "$S/worker/ConnectionStrings__ragdb"
[ -f "$S/.pepper" ] || openssl rand -hex 48 > "$S/.pepper"
for d in migrations api admin; do tr -d '\n' < "$S/.pepper" > "$S/$d/Security__KeyPepper"; done

# --- The api's and the worker's own keys --------------------------------------------------------------------------
[ -f "$S/api/Security__DownloadGrantKeys__k1" ] || rand > "$S/api/Security__DownloadGrantKeys__k1"
[ -f "$S/api/Security__CacheKeyPepper" ] || rand > "$S/api/Security__CacheKeyPepper"
[ -f "$S/api/Security__ConversationIdKeys__k1" ] || rand > "$S/api/Security__ConversationIdKeys__k1"
# Open WebUI's user JWT secret: unused without Open WebUI, but rag-api expects the file (identity design §5.2).
[ -f "$S/.openwebui-jwt-secret" ] || rand > "$S/.openwebui-jwt-secret"
tr -d '\n' < "$S/.openwebui-jwt-secret" > "$S/api/OpenWebUi__JwtSecret"
# Open WebUI's own files (profile openwebui; harmless without it): the same user-JWT secret, its session key, the
# feedback service account's password, and a CA bundle with the public CAs (its OIDC provider), RAG's (rag-api) and the
# gateway's.
tr -d '\n' < "$S/.openwebui-jwt-secret" > "$S/openwebui/jwt-secret"
[ -f "$S/openwebui/webui_secret_key" ] || rand > "$S/openwebui/webui_secret_key"
[ -f "$S/openwebui/admin-password" ] || rand | tr -d '\n' > "$S/openwebui/admin-password"
cat /etc/ssl/certs/ca-certificates.crt "$C/ca.pem" /gateway-certs/ca.pem > "$S/openwebui/ca.pem"
[ -f "$S/worker/Security__CrawlKeyPepper" ] || rand | tr -d '\n' > "$S/worker/Security__CrawlKeyPepper"
# The renderer's secret WebSocket path (connectors design §8.3): the worker gets the endpoint only while the render
# profile is on, so without the renderer it refuses rendering instead of waiting for a browser that never starts.
[ -f "$S/.render-ws-path" ] || rand > "$S/.render-ws-path"
printf '/%s' "$(tr -d '\r\n' < "$S/.render-ws-path")" > "$S/renderer/ws-path"
case ",${COMPOSE_PROFILES:-}," in
  *,render,*) printf 'ws://rag-renderer:3000/%s' "$(tr -d '\r\n' < "$S/.render-ws-path")" > "$S/worker/Render__Endpoint" ;;
  *) rm -f "$S/worker/Render__Endpoint" ;;
esac
# Blob master key of the file key provider (plan §8a): without it the `internal` blobs cannot be read. Back it up.
if [ ! -f "$S/keys/rag-blob-internal" ]; then
  openssl rand -base64 32 > "$S/keys/rag-blob-internal"
  chmod 0400 "$S/keys/rag-blob-internal"
fi

# --- Files that follow a variable ------------------------------------------------------------------------------
# put SERVICE NAME VALUE: the secret file NAME of SERVICE holds VALUE; an empty VALUE removes it.
put() {
  if [ -n "$3" ]; then printf '%s' "$3" > "$S/$1/$2"; else rm -f "$S/$1/$2"; fi
}
# Each service's credential key of the gateway integration Ume.Rag (identity design §4.7):
# UME_RAG_API_GATEWAY_KEY, UME_RAG_ADMIN_GATEWAY_KEY and UME_RAG_WORKER_GATEWAY_KEY. The per-level keys they
# replace are revoked at the gateway and read by no service, so none is left in the volume.
for svc in api admin worker; do
  up="$(echo "$svc" | tr '[:lower:]' '[:upper:]')"
  put "$svc" Gateway__Key "$(printenv "UME_RAG_${up}_GATEWAY_KEY" || true)"
  rm -f "$S/$svc"/Gateway__Keys__*
done
put admin Oidc__ClientSecret "${UME_RAG_OIDC_CLIENT_SECRET:-}"
# The feedback service account's Open WebUI key, for rag-api's feedback import (chat design §13.3); empty = none.
put api OpenWebUi__AdminApiKey "${UME_RAG_OPENWEBUI_ADMIN_API_KEY:-}"
# The Data API key of the admin's former org sync: the admin syncs with its credential key now.
if [ -e "$S/admin/OrgSync__DataApiKey" ]; then
  rm -f "$S/admin/OrgSync__DataApiKey"
  echo "rag-bootstrap: deleted admin/OrgSync__DataApiKey; ask the gateway admin to revoke that Data API key"
fi

# --- Static configuration (the services run read-only, so they get it from a volume) -----------------------------
mkdir -p "$F/postgres-init" "$F/postgres" "$F/migrations"
cp /config/postgres-init.sql "$F/postgres-init/10-roles.sql"
cp /config/pg_hba.conf "$F/postgres/pg_hba.conf"
cp /config/postgresql.conf "$F/postgres/postgresql.conf"
cp /config/app-roles.sql "$F/migrations/app-roles.sql"
chmod -R a+rX "$F"

# --- Blob container (issue #41): rag/ and the worker's spool, owned by the services' uid ----------------------------
mkdir -p /blobs/rag /blobs/spool
chown 1654:1654 /blobs/rag /blobs/spool
chmod 0750 /blobs/rag /blobs/spool

chown -R 999:999 "$C/postgres" "$S/postgres"
chown -R 10001:10001 "$C/parser"
chown -R 10002:10002 "$S/renderer"
chown -R 1000:1000 "$S/openwebui" /openwebui-data
chmod 0700 "$S/openwebui"
chown -R 1654:1654 "$C/api" "$C/admin" "$C/worker" "$C/migrations" "$S/api" "$S/admin" "$S/worker" "$S/migrations" "$S/keys"
chmod 755 "$C" "$S"
echo "rag-bootstrap: certificates and secrets ready"

# --- What is still missing: a checklist at the end of the log (never fatal: the stack starts and waits) ------------
todo=""
note() { todo="$todo
  - $1"; }
example() { case "$1" in ""|*example.se*|*CHANGE*) return 0 ;; esac; return 1; }
for svc in api admin worker; do
  up="$(echo "$svc" | tr '[:lower:]' '[:upper:]')"
  [ -e "$S/$svc/Gateway__Key" ] || note "UME_RAG_${up}_GATEWAY_KEY: issue rag-$svc's credential key in the gateway's admin (integration Ume.Rag) and update the stack; no restart needed"
done
example "$UME_RAG_API_HOST" && note "UME_RAG_API_HOST is still an example value"
# Optional: unset, rag-api's public URL is https://<UME_RAG_API_HOST>.
case "${UME_RAG_API_PUBLIC_URL:-}" in "") ;;
  https://*example.se*|https://*CHANGE*) note "UME_RAG_API_PUBLIC_URL is still an example value" ;;
  https://*) ;; *) note "UME_RAG_API_PUBLIC_URL must be the https URL users reach rag-api at (citation links use it)" ;; esac
example "${UME_RAG_EMBEDDING_MODEL:-}" && note "UME_RAG_EMBEDDING_MODEL: the gateway's embedding model id (seeded once as the platform's profile)"
example "${UME_RAG_OIDC_CLIENT_SECRET:-}" && note "UME_RAG_OIDC_CLIENT_SECRET: sign-in to Ume.Rag's admin fails without it (restart rag-admin after setting it)"
case ",${COMPOSE_PROFILES:-}," in
  *,clamav,*) ;;
  *) [ "${UME_RAG_CLAMAV_MODE:-on}" = off ] || note "COMPOSE_PROFILES lacks clamav while UME_RAG_CLAMAV_MODE is on: uploads wait with clamav.unavailable" ;;
esac
case ",${COMPOSE_PROFILES:-}," in
  *,openwebui,*)
    example "${UME_RAG_OPENWEBUI_HOST:-}" && note "UME_RAG_OPENWEBUI_HOST: the chat's public host name"
    example "${UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET:-}" && note "UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET: users can't sign in to Open WebUI"
    if [ -z "${UME_RAG_OPENWEBUI_API_KEY:-}" ] || [ -z "${UME_RAG_OPENWEBUI_CLIENT_ID:-}" ]; then
      note "UME_RAG_OPENWEBUI_API_KEY and UME_RAG_OPENWEBUI_CLIENT_ID: create Open WebUI's client in Ume.Rag's admin (Platform > Clients, kind open-webui)"
    fi ;;
esac
if [ -n "$todo" ]; then
  echo "rag-bootstrap: still to do (docs/portainer.md):$todo"
else
  echo "rag-bootstrap: every setting it checks is in place"
fi
# Not a setting to check here: who is a platform admin comes from the sign-in token.
echo "rag-bootstrap: the first platform admin needs the role rag-platform-admin in the token's roles claim (Keycloak: give the user that realm role). Signed in without it, Ume.Rag's admin says you have no role: then give the role, or name a group of the token's in UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN"
