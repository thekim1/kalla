#!/bin/sh
# One-shot bootstrap of the RAG part of compose.portainer.yaml (the gateway's stack plus RAG, one Portainer stack).
# The Portainer counterpart of deploy/init-deployment.sh, after the gateway's deploy/portainer/bootstrap.sh: creates
# RAG's internal CA, its service certificates, the Data Protection certificate, the database passwords, the key pepper,
# the api's keys, rag-admin's setup secret, the worker's crawl key pepper, the renderer's WebSocket path and the blob
# master key in the rag-certs and rag-secrets volumes, and copies the static configuration into rag-config. Idempotent:
# existing secrets are never overwritten (a new pepper, key or Data Protection certificate would break existing data);
# service certificates are re-issued when they expire within 30 days or their host names change (the checklist at the
# end names the services to restart, and every certificate that expires within 60 days). Files that follow a variable
# (the gateway credential keys, also taken from the gateway's manifest secrets; the OIDC client secrets and refusing
# guests, when the stack declares its sign-in instead of the setup wizard; the names of the retired variables the stack
# still sets) are written again on every start, and removed when the variable is empty.
#
# Trust: the services that call out (rag-api, rag-admin, rag-worker) trust RAG's CA plus the gateway's (its ume-certs
# volume, mounted at /gateway-certs), so https://gateway:8443 verifies, and every *.pem in /trust (an operator's CA
# for a gateway or a sign-in provider elsewhere).
#
# The one bootstrap of every deployment form (Decisions "One deployment bootstrap"): the Portainer stack and Compose
# run it as the rag-bootstrap service, and deploy/kubernetes/create-secrets.sh runs it on the operator's machine. What
# differs between them comes in these variables; unset, each has the stack's value:
#   UME_RAG_BOOTSTRAP_CERTS, _SECRETS, _CONF   where it writes (/certs, /secrets, /conf); an empty _CONF writes no
#                                              static configuration
#   UME_RAG_BOOTSTRAP_CONFIG                   where the static configuration is read from (/config)
#   UME_RAG_BOOTSTRAP_GATEWAY_CERTS            the directory with the gateway's ca.pem (/gateway-certs);
#   UME_RAG_BOOTSTRAP_GATEWAY_CA=optional      a missing one is then no error (a gateway with a public certificate)
#   UME_RAG_BOOTSTRAP_TRUST                    more CA certificates to trust, *.pem (/trust); rag-migrate gets them
#                                              too (a database with a certificate from another CA)
#   UME_RAG_BOOTSTRAP_INTEGRATION_SECRETS      the gateway's manifest keys (/integration-secrets)
#   UME_RAG_BOOTSTRAP_PKI                      the operator's own certificates for the public listeners: api.crt and
#                                              api.key, admin.crt and admin.key (/pki); a pair found there is used
#                                              instead of one from the internal CA
#   UME_RAG_BOOTSTRAP_OWN=no                   leave every file owned by the caller and create no blob directories
#                                              (Kubernetes: the files become secrets, never mounts)
#   UME_RAG_POSTGRES_HOST                      the database's host name in its certificate and the connection strings
#                                              (rag-postgres)
#   UME_RAG_BACKUP_TARGET                      a pgBackRest repository: s3://ENDPOINT/BUCKET[/PATH] (with
#                                              UME_RAG_BACKUP_S3_KEY, UME_RAG_BACKUP_S3_KEY_SECRET and
#                                              UME_RAG_BACKUP_S3_REGION, default us-east-1), or anything else for a
#                                              posix repository at /var/lib/pgbackrest. Unset (the Portainer stack):
#                                              no repository settings
# And what only a Compose host without the gateway in its stack has (deploy/compose.rag.yaml and its overlays); unset,
# nothing of it happens:
#   UME_RAG_BOOTSTRAP_PROXY_GATEWAY=no         the TLS proxy has no blocks for the gateway and its admin
#   UME_RAG_HOST_MEMORY=24                     the 24 GB host's Postgres memory settings in rag-config's
#                                              postgresql.conf, and ClamAV's 24 GB lines (below)
#   clamd.conf and freshclam.conf in _CONFIG   the ClamAV image's confs, written to rag-config/clamav for rag-clamav,
#                                              with UME_RAG_FRESHCLAM_PROXY=HOST:PORT as freshclam's HTTP proxy
#   /backup/pgbackrest, /backup/uploads        the pgBackRest repository and the upload copy, when mounted: given to
#                                              Postgres (999) and to the services' uid (1654)
set -eu
umask 077
C="${UME_RAG_BOOTSTRAP_CERTS:-/certs}"
S="${UME_RAG_BOOTSTRAP_SECRETS:-/secrets}"
F="${UME_RAG_BOOTSTRAP_CONF-/conf}"
K="${UME_RAG_BOOTSTRAP_CONFIG:-/config}"
G="${UME_RAG_BOOTSTRAP_GATEWAY_CERTS:-/gateway-certs}"
T="${UME_RAG_BOOTSTRAP_TRUST:-/trust}"
P="${UME_RAG_BOOTSTRAP_PKI:-/pki}"
OWN="${UME_RAG_BOOTSTRAP_OWN:-yes}"
PG="${UME_RAG_POSTGRES_HOST:-rag-postgres}"
W="$(mktemp -d)"; trap 'rm -rf "$W"' EXIT
# The host names come resolved from the stack file: UME_DOMAIN's defaults unless a UME_RAG_*_HOST is set.
: "${UME_RAG_API_HOST:?Set UME_DOMAIN or UME_RAG_API_HOST (public hostname of rag-api)}"
ADMIN_HOST="${UME_RAG_ADMIN_HOST:-$UME_RAG_API_HOST}"
for d in postgres api admin worker parser migrations; do mkdir -p "$C/$d"; done
for d in postgres api admin worker migrations keys renderer openwebui openwebui-feedback; do mkdir -p "$S/$d"; done
rand() { openssl rand -hex 32; }
quiet() { "$@" >/dev/null 2>&1; }

if [ ! -f "$C/ca.pem" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout "$C/ca.key" -out "$C/ca.pem" -days 3650 -subj /CN=ume-rag-internal-ca \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign"
fi

# issue DIR SAN-LIST: (re)issue unless a valid certificate for exactly these names, from the current CA, exists. A
# re-issued certificate (not a first one) is listed in $reissued, for the checklist.
reissued=""
issue() {
  svc="$1"; san="$2"; dir="$C/$svc"
  if [ -f "$dir/$svc.crt" ] && [ "$(cat "$dir/.san" 2>/dev/null)" = "$san" ] &&
     quiet openssl x509 -checkend 2592000 -noout -in "$dir/$svc.crt" && quiet openssl verify -CAfile "$C/ca.pem" "$dir/$svc.crt"; then
    return
  fi
  [ -f "$C/ca.key" ] || { echo "rag-bootstrap: $C/ca.key is needed to issue the certificate of $svc: copy it back from where you keep it" >&2; exit 1; }
  [ ! -f "$dir/$svc.crt" ] || reissued="$reissued $svc"
  echo "rag-bootstrap: issuing certificate for $svc ($san)"
  quiet openssl req -newkey rsa:3072 -nodes -keyout "$dir/$svc.key" -out "$W/$svc.csr" -subj "/CN=$svc"
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n' \
    "$san" > "$W/$svc.ext"
  quiet openssl x509 -req -in "$W/$svc.csr" -CA "$C/ca.pem" -CAkey "$C/ca.key" -CAcreateserial -CAserial "$W/ca.srl" \
    -out "$dir/$svc.crt" -days 825 -extfile "$W/$svc.ext"
  printf '%s' "$san" > "$dir/.san"
}
# public SVC SAN-LIST: the operator's own certificate when $P has the pair (copied on every run, so a renewed one is
# picked up), else one from the internal CA. Without the pair, a certificate that came from $P is re-issued.
public() {
  if [ -f "$P/$1.crt" ] && [ -f "$P/$1.key" ]; then
    cp "$P/$1.crt" "$C/$1/$1.crt"; cp "$P/$1.key" "$C/$1/$1.key"; printf 'operator' > "$C/$1/.san"
  else
    issue "$1" "$2"
  fi
}
issue postgres "DNS:$PG,DNS:localhost"
public api "DNS:rag-api,DNS:localhost,DNS:$UME_RAG_API_HOST"
public admin "DNS:rag-admin,DNS:localhost,DNS:$ADMIN_HOST"
issue worker "DNS:rag-worker,DNS:localhost"
issue parser "DNS:rag-parser,DNS:localhost"

if [ ! -f "$C/data-protection.pfx" ]; then
  quiet openssl req -x509 -newkey rsa:3072 -nodes -keyout "$W/dp.key" -out "$W/dp.crt" -days 3650 -subj /CN=ume-rag-data-protection
  quiet openssl pkcs12 -export -inkey "$W/dp.key" -in "$W/dp.crt" -out "$C/data-protection.pfx" -passout pass:
fi
for d in api admin migrations; do cp "$C/data-protection.pfx" "$C/$d/data-protection.pfx"; done
for d in postgres parser migrations; do cp "$C/ca.pem" "$C/$d/ca.pem"; done
# What the services that call out trust besides RAG's CA: the gateway's CA and the operator's (see the header).
cp "$C/ca.pem" "$W/trust.pem"
if [ -f "$G/ca.pem" ]; then
  cat "$G/ca.pem" >> "$W/trust.pem"
elif [ "${UME_RAG_BOOTSTRAP_GATEWAY_CA:-}" != optional ]; then
  echo "rag-bootstrap: the gateway's CA ($G/ca.pem) is missing" >&2; exit 1
fi
# rag-migrate calls only the database: it gets the operator's CAs too, for a database with a certificate of its own.
for f in "$T"/*.pem; do
  [ -f "$f" ] || continue
  cat "$f" >> "$W/trust.pem"; cat "$f" >> "$C/migrations/ca.pem"
done
for d in api admin worker; do cp "$W/trust.pem" "$C/$d/ca.pem"; done

# --- Database (connection pools per instance, data-model §1.1: api 12, admin 5, worker 10, migrator 2) ---------------
for role in bootstrap migrator api admin worker; do
  [ -f "$S/postgres/${role}_password" ] || rand > "$S/postgres/${role}_password"
done
connection() {
  printf 'Host=%s;Database=ragdb;Username=ume_rag_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable;Maximum Pool Size=%s' \
    "$PG" "$1" "$(tr -d '\r\n' < "$S/postgres/${1}_password")" "$2"
}
connection migrator 2 > "$S/migrations/ConnectionStrings__ragdb"
connection api 12 > "$S/api/ConnectionStrings__ragdb"
connection admin 5 > "$S/admin/ConnectionStrings__ragdb"
connection worker 10 > "$S/worker/ConnectionStrings__ragdb"
[ -f "$S/.pepper" ] || openssl rand -hex 48 > "$S/.pepper"
for d in migrations api admin; do tr -d '\n' < "$S/.pepper" > "$S/$d/Security__KeyPepper"; done

# --- The api's, rag-admin's and the worker's own keys -------------------------------------------------------------
[ -f "$S/api/Security__DownloadGrantKeys__k1" ] || rand > "$S/api/Security__DownloadGrantKeys__k1"
[ -f "$S/api/Security__CacheKeyPepper" ] || rand > "$S/api/Security__CacheKeyPepper"
[ -f "$S/api/Security__ConversationIdKeys__k1" ] || rand > "$S/api/Security__ConversationIdKeys__k1"
# rag-admin's setup secret (identity design §19): the setup code is derived from it. Kept, but not worth backing up: a
# new one only changes the setup codes printed from then on.
[ -f "$S/admin/Setup__Secret" ] || rand > "$S/admin/Setup__Secret"
# Open WebUI's user JWT secret: unused without Open WebUI, but rag-api expects the file (identity design §5.2).
[ -f "$S/.openwebui-jwt-secret" ] || rand > "$S/.openwebui-jwt-secret"
tr -d '\n' < "$S/.openwebui-jwt-secret" > "$S/api/OpenWebUi__JwtSecret"
# Open WebUI's own files (profile openwebui; harmless without it): the same user-JWT secret, its session key, the
# feedback service account's password, and a CA bundle with the public CAs (its OIDC provider), RAG's (rag-api) and the
# gateway's.
tr -d '\n' < "$S/.openwebui-jwt-secret" > "$S/openwebui/jwt-secret"
[ -f "$S/openwebui/webui_secret_key" ] || rand > "$S/openwebui/webui_secret_key"
[ -f "$S/openwebui/admin-password" ] || rand | tr -d '\n' > "$S/openwebui/admin-password"
# (the public CAs are the image's; outside a container the bundle may be elsewhere, and Open WebUI is not run from there)
{ [ ! -r /etc/ssl/certs/ca-certificates.crt ] || cat /etc/ssl/certs/ca-certificates.crt; cat "$W/trust.pem"; } > "$S/openwebui/ca.pem"
[ -f "$S/worker/Security__CrawlKeyPepper" ] || rand | tr -d '\n' > "$S/worker/Security__CrawlKeyPepper"
# The renderer's secret WebSocket path (connectors design §8.3): the worker gets the endpoint only while the render
# profile is on, so without the renderer it refuses rendering instead of waiting for a browser that never starts.
[ -f "$S/.render-ws-path" ] || rand > "$S/.render-ws-path"
printf '/%s' "$(tr -d '\r\n' < "$S/.render-ws-path")" > "$S/renderer/ws-path"
case ",${COMPOSE_PROFILES:-}," in
  *,render,*) printf 'ws://rag-renderer:3000/%s' "$(tr -d '\r\n' < "$S/.render-ws-path")" > "$S/worker/Render__Endpoint" ;;
  *) rm -f "$S/worker/Render__Endpoint" ;;
esac
# The malware scan follows the clamav profile (M-17 C3): on while rag-clamav runs; off without it, so files are stored
# unscanned and marked so (#535) instead of waiting for a scanner that never starts.
case ",${COMPOSE_PROFILES:-}," in
  *,clamav,*) printf on > "$S/worker/ClamAv__Mode" ;;
  *) printf off > "$S/worker/ClamAv__Mode" ;;
esac
# Open WebUI's address for rag-api's feedback import (M-17 C2): the stack's own rag-openwebui while the openwebui
# profile is on. The import also needs the feedback key, which rag-openwebui-setup mints (openwebui-feedback, below).
# Open WebUI's key for rag-api (M-17 C4, identity design §5.2): generated once; Open WebUI's start-up wrapper exports it
# as OPENAI_API_KEYS, and rag-migrate registers it as the deployment's own Open WebUI client while the profile is on. A
# new key: delete .openwebui-rag-api-key in the volume and update the stack (rag-migrate revokes the old one).
[ -f "$S/.openwebui-rag-api-key" ] || printf 'ume-rag-sk-%s' "$(openssl rand -base64 96 | tr -dc 'A-Za-z0-9' | head -c 43)" > "$S/.openwebui-rag-api-key"
cp "$S/.openwebui-rag-api-key" "$S/openwebui/rag-api-key"
# rag-admin's setup wizard offers Open WebUI's sign-in step only while it runs (OpenWebUi__Enabled, identity design §19).
case ",${COMPOSE_PROFILES:-}," in
  *,openwebui,*)
    printf 'http://rag-openwebui:8080' > "$S/api/OpenWebUi__BaseUrl"
    printf true > "$S/admin/OpenWebUi__Enabled"
    cp "$S/.openwebui-rag-api-key" "$S/migrations/OpenWebUi__ApiKey" ;;
  *) rm -f "$S/api/OpenWebUi__BaseUrl" "$S/admin/OpenWebUi__Enabled" "$S/migrations/OpenWebUi__ApiKey" ;;
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
# Each service's credential key of the gateway integration Ume.Rag (identity design §4.7). From the gateway's
# manifest (M-17 C7): the gateway's migrations provision the integration from UME_INTEGRATION_MANIFEST and write each
# new key's secret to the integration-secrets volume as Ume.Rag/rag-<service>; that volume is mounted read-only at
# /integration-secrets. UME_RAG_API_GATEWAY_KEY, UME_RAG_ADMIN_GATEWAY_KEY and UME_RAG_WORKER_GATEWAY_KEY, when set,
# win over the files (keys issued by hand, or a gateway elsewhere). The per-level keys they replace are revoked at the
# gateway and read by no service, so none is left in the volume.
I="${UME_RAG_BOOTSTRAP_INTEGRATION_SECRETS:-/integration-secrets}/Ume.Rag"
for svc in api admin worker; do
  up="$(echo "$svc" | tr '[:lower:]' '[:upper:]')"
  key="$(printenv "UME_RAG_${up}_GATEWAY_KEY" || true)"
  if [ -z "$key" ] && [ -s "$I/rag-$svc" ]; then key="$(cat "$I/rag-$svc")"; fi
  put "$svc" Gateway__Key "$key"
  rm -f "$S/$svc"/Gateway__Keys__*
done
put admin Oidc__ClientSecret "${UME_RAG_OIDC_CLIENT_SECRET:-}"
# Refusing guests as a deployment value (identity design §19): a file, not rag-admin's environment, where an empty
# boolean would stop it at start. Unset, the setup wizard's choice applies, else the default (refused). Anything but true
# or false is left out and named on the checklist.
case "${UME_RAG_OIDC_REFUSE_GUESTS:-}" in
  ""|true|false) put admin Oidc__RefuseGuests "${UME_RAG_OIDC_REFUSE_GUESTS:-}" ;;
  *) rm -f "$S/admin/Oidc__RefuseGuests" ;;
esac
# Open WebUI's client secret as a deployment value (identity design §19): rag-api serves it to Open WebUI's start, rag-admin
# shows that it is set. Unset, the setup wizard's stored secret applies.
put api OpenWebUi__Oidc__ClientSecret "${UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET:-}"
put admin OpenWebUi__Oidc__ClientSecret "${UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET:-}"
# Retired variables the stack still sets (Decisions "Deployment values across releases"): the stack file lists their
# names in UME_RAG_RETIRED_SET. They are named on the checklist with what replaced each, and rag-admin gets the names
# (Deployment:RetiredVariables) for the notice on its Home page. A variable that is no longer read is added here and
# to UME_RAG_RETIRED_SET in the stack template in the same change.
retired_hint() {
  case "$1" in
    UME_RAG_ORG_SYNC_*) echo "rag-admin syncs the organisation with its gateway credential key" ;;
    UME_RAG_OPENWEBUI_API_KEY|UME_RAG_OPENWEBUI_CLIENT_ID) echo "the deployment creates Open WebUI's client itself" ;;
    UME_RAG_OPENWEBUI_ADMIN_API_KEY|UME_RAG_OPENWEBUI_PASSWORD_AUTH) echo "rag-openwebui-setup mints the feedback key" ;;
    UME_RAG_OPENWEBUI_BASE_URL) echo "the feedback address follows the openwebui profile" ;;
    UME_RAG_CLAMAV_MODE) echo "the malware scan follows the clamav profile" ;;
    UME_RAG_EMBEDDING_*) echo "the embedding profile is created in the admin UI, under Models" ;;
    UME_RAG_GATEWAY_MAX_RESIDENCY) echo "the ceiling is each credential key's allowed residencies in the gateway" ;;
    UME_RAG_OIDC_GROUPS_*) echo "role groups are set in the admin UI, under Settings" ;;
    UME_ORGANISATION_NAME) echo "the organisation's name comes from the gateway's branding" ;;
    UME_RAG_GATEWAY_INTEGRATION_API_URL) echo "the address comes from the gateway's UME_INTEGRATION_API_URL" ;;
    *) echo "see docs/upgrade-guide.md" ;;
  esac
}
retired=""
for name in ${UME_RAG_RETIRED_SET:-}; do
  case "$name" in *[!A-Z0-9_]*) continue ;; esac
  retired="$retired,$name"
done
retired="${retired#,}"
put admin Deployment__RetiredVariables "$retired"
# The feedback service account's Open WebUI key (chat design §13.3) is minted by rag-openwebui-setup into
# openwebui-feedback (M-17 C6); the hand-made key of UME_RAG_OPENWEBUI_ADMIN_API_KEY that rag-api read before goes.
if [ -e "$S/api/OpenWebUi__AdminApiKey" ]; then
  rm -f "$S/api/OpenWebUi__AdminApiKey"
  echo "rag-bootstrap: deleted api/OpenWebUi__AdminApiKey; rag-openwebui-setup mints the feedback key now"
fi
# The Data API key of the admin's former org sync: the admin syncs with its credential key now.
if [ -e "$S/admin/OrgSync__DataApiKey" ]; then
  rm -f "$S/admin/OrgSync__DataApiKey"
  echo "rag-bootstrap: deleted admin/OrgSync__DataApiKey; ask the gateway admin to revoke that Data API key"
fi

# --- pgBackRest repository (repo-and-deployment §7.4), only where the deployment names a backup target ---------------
# The passphrase is generated once (a new one could not read the existing backups: escrow it); repo.conf is written
# again from it and the variables on every run.
if [ -n "${UME_RAG_BACKUP_TARGET:-}" ]; then
  mkdir -p "$S/pgbackrest"
  [ -f "$S/pgbackrest/cipher-pass" ] || openssl rand -base64 48 | tr -d '\n' > "$S/pgbackrest/cipher-pass"
  {
    echo "# Written by rag-bootstrap (repo-and-deployment §7.4); the passphrase is pgbackrest/cipher-pass."
    echo "[global]"
    echo "repo1-cipher-type=aes-256-cbc"
    echo "repo1-cipher-pass=$(cat "$S/pgbackrest/cipher-pass")"
    case "$UME_RAG_BACKUP_TARGET" in
      s3://*)
        location="${UME_RAG_BACKUP_TARGET#s3://}"; endpoint="${location%%/*}"; bucket_path="${location#*/}"
        if [ "$bucket_path" = "$location" ] || [ -z "$bucket_path" ]; then
          echo "rag-bootstrap: UME_RAG_BACKUP_TARGET is s3://ENDPOINT/BUCKET[/PATH]" >&2; exit 1
        fi
        if [ -z "${UME_RAG_BACKUP_S3_KEY:-}" ] || [ -z "${UME_RAG_BACKUP_S3_KEY_SECRET:-}" ]; then
          echo "rag-bootstrap: an s3:// backup target needs UME_RAG_BACKUP_S3_KEY and UME_RAG_BACKUP_S3_KEY_SECRET" >&2; exit 1
        fi
        bucket="${bucket_path%%/*}"; path="${bucket_path#"$bucket"}"; path="${path#/}"; path="${path%/}"
        echo "repo1-type=s3"
        echo "repo1-path=/${path:-ume-rag}"
        echo "repo1-s3-endpoint=$endpoint"
        echo "repo1-s3-bucket=$bucket"
        echo "repo1-s3-region=${UME_RAG_BACKUP_S3_REGION:-us-east-1}"
        echo "repo1-s3-key=$UME_RAG_BACKUP_S3_KEY"
        echo "repo1-s3-key-secret=$UME_RAG_BACKUP_S3_KEY_SECRET"
        echo "repo1-s3-uri-style=path" ;;
      *)
        echo "repo1-type=posix"
        echo "repo1-path=/var/lib/pgbackrest" ;;
    esac
  } > "$S/pgbackrest/repo.conf"
fi

# --- Static configuration (the services run read-only, so they get it from a volume) -----------------------------
proxy_todo=""
if [ -n "$F" ]; then
mkdir -p "$F/postgres-init" "$F/postgres" "$F/migrations" "$F/openwebui-setup" "$F/openwebui"
# rag-openwebui-setup's script and rag-openwebui's start wrapper (profile openwebui): those services are read-only, so they
# read their scripts from rag-config.
cp "$K"/feedback-key-setup.py "$F/openwebui-setup/feedback-key-setup.py"
cp "$K"/start-with-sign-in.py "$F/openwebui/start-with-sign-in.py"
cp "$K"/postgres-init.sql "$F/postgres-init/10-roles.sql"
cp "$K"/pg_hba.conf "$F/postgres/pg_hba.conf"
cp "$K"/postgresql.conf "$F/postgres/postgresql.conf"
# A 24 GB host (repo-and-deployment §6.2): the memory settings postgresql.conf names in its comments.
if [ "${UME_RAG_HOST_MEMORY:-16}" = 24 ]; then
  sed -e 's/^shared_buffers = [0-9A-Za-z]*/shared_buffers = 1536MB/' -e 's/^effective_cache_size = [0-9A-Za-z]*/effective_cache_size = 3500MB/' \
    -e 's/^maintenance_work_mem = [0-9A-Za-z]*/maintenance_work_mem = 1GB/' "$K/postgresql.conf" > "$F/postgres/postgresql.conf"
  for s in shared_buffers=1536MB effective_cache_size=3500MB maintenance_work_mem=1GB; do
    grep -q "^${s%%=*} = ${s#*=}" "$F/postgres/postgresql.conf" || { echo "rag-bootstrap: postgresql.conf has no ${s%%=*} line to set for a 24 GB host" >&2; exit 1; }
  done
fi
cp "$K"/app-roles.sql "$F/migrations/app-roles.sql"
# ClamAV's confs, only where the deployment gives the image's (repo-and-deployment §5.5): the image's unprivileged
# entrypoint takes a setting only from a mounted file. A 24 GB host (rag-clamav's 3g) keeps the old engine during a
# reload and test-loads updates before using them; a freshclam proxy is for a host whose egress goes through one.
if [ -f "$K/clamd.conf" ] && [ -f "$K/freshclam.conf" ]; then
  mkdir -p "$F/clamav"
  cp "$K/clamd.conf" "$F/clamav/clamd.conf"; cp "$K/freshclam.conf" "$F/clamav/freshclam.conf"
  if [ "${UME_RAG_HOST_MEMORY:-16}" = 24 ]; then
    sed 's/^ConcurrentDatabaseReload no$/ConcurrentDatabaseReload yes/' "$K/clamd.conf" > "$F/clamav/clamd.conf"
    sed 's/^TestDatabases no$/TestDatabases yes/' "$K/freshclam.conf" > "$F/clamav/freshclam.conf"
    if ! grep -qx 'ConcurrentDatabaseReload yes' "$F/clamav/clamd.conf" || ! grep -qx 'TestDatabases yes' "$F/clamav/freshclam.conf"; then
      echo "rag-bootstrap: the ClamAV confs no longer have the lines a 24 GB host changes" >&2; exit 1
    fi
  fi
  fp="${UME_RAG_FRESHCLAM_PROXY:-}"
  if [ -n "$fp" ]; then
    printf '%s' "$fp" | grep -Eq '^[A-Za-z0-9.-]+:[0-9]+$' || { echo "rag-bootstrap: UME_RAG_FRESHCLAM_PROXY is HOST:PORT" >&2; exit 1; }
    printf '\n# Egress through a proxy (UME_RAG_FRESHCLAM_PROXY).\nHTTPProxyServer %s\nHTTPProxyPort %s\n' "${fp%:*}" "${fp##*:}" \
      >> "$F/clamav/freshclam.conf"
  fi
else
  rm -rf "$F/clamav"
fi
# The TLS proxy's nginx.conf (profile proxy, M-17 C8): the template with the stack's host names. A host without a name
# of its own (the gateway's admin on the gateway's name, rag-admin on rag-api's) gets no block, nor Open WebUI without
# its profile; the checklist at the end says so.
mkdir -p "$F/proxy"
case ",${COMPOSE_PROFILES:-}," in
  *,proxy,*)
    hostname_ok() { printf '%s' "$1" | grep -Eq '^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$'; }
    gw="${UME_GATEWAY_HOST:-}"; gw_admin="${UME_ADMIN_HOST:-}"; api="$UME_RAG_API_HOST"; rag_admin="${UME_RAG_ADMIN_HOST:-}"
    owui_host="${UME_RAG_OPENWEBUI_HOST:-}"
    # Every block has a marker and is written only for a valid host name, and only validated names reach sed (#711): a
    # URL or a typo leaves that block out and names it on the checklist instead of stopping rag-bootstrap.
    # Without the gateway in the stack (UME_RAG_BOOTSTRAP_PROXY_GATEWAY=no) neither gateway block, and nothing to note.
    with_gateway="${UME_RAG_BOOTSTRAP_PROXY_GATEWAY:-yes}"
    if [ "$with_gateway" = no ]; then gw=""; enable="-e /^#gateway/d"
    elif hostname_ok "$gw"; then enable="-e s/^#gateway//"
    else gw=""; enable="-e /^#gateway/d"; proxy_todo="$proxy_todo|UME_GATEWAY_HOST is not a host name, so the proxy has no gateway block"; fi
    if hostname_ok "$api"; then enable="$enable -e s/^#ragapi//"
    else api=""; enable="$enable -e /^#ragapi/d"; proxy_todo="$proxy_todo|UME_RAG_API_HOST is not a host name, so the proxy has no rag-api block"; fi
    if [ "$with_gateway" = no ]; then gw_admin=""; enable="$enable -e /^#gwadmin/d"
    elif hostname_ok "$gw_admin" && [ "$gw_admin" != "$gw" ]; then enable="$enable -e s/^#gwadmin//"
    else gw_admin=""; enable="$enable -e /^#gwadmin/d"
      proxy_todo="$proxy_todo|UME_ADMIN_HOST: the proxy serves the gateway's admin only on a host name of its own"; fi
    if hostname_ok "$rag_admin" && [ "$rag_admin" != "$api" ]; then enable="$enable -e s/^#ragadmin//"
    else rag_admin=""; enable="$enable -e /^#ragadmin/d"
      proxy_todo="$proxy_todo|UME_RAG_ADMIN_HOST: the proxy serves Ume.Rag's admin only on a host name of its own"; fi
    case ",${COMPOSE_PROFILES:-}," in
      *,openwebui,*)
        if hostname_ok "$owui_host"; then enable="$enable -e s/^#owui//"
        else owui_host=""; enable="$enable -e /^#owui/d"; proxy_todo="$proxy_todo|UME_RAG_OPENWEBUI_HOST: the proxy has no Open WebUI block"; fi ;;
      *) owui_host=""; enable="$enable -e /^#owui/d" ;;
    esac
    # shellcheck disable=SC2086 # $enable is a list of sed expressions
    sed $enable -e "s/@GATEWAY_HOST@/$gw/g" -e "s/@ADMIN_HOST@/$gw_admin/g" -e "s/@API_HOST@/$api/g" \
      -e "s/@RAG_ADMIN_HOST@/$rag_admin/g" -e "s/@OPENWEBUI_HOST@/$owui_host/g" "$K/nginx-proxy.conf.template" > "$F/proxy/nginx.conf"
    # The internal CAs it verifies the upstreams against (public certificates only; ume-certs also holds private keys).
    cp "$C/ca.pem" "$F/proxy/rag-ca.pem"
    [ ! -f "$G/ca.pem" ] || cp "$G/ca.pem" "$F/proxy/gateway-ca.pem"
    echo "rag-bootstrap: wrote the TLS proxy's nginx.conf" ;;
  *) rm -f "$F/proxy/nginx.conf" "$F/proxy/rag-ca.pem" "$F/proxy/gateway-ca.pem" ;;
esac
chmod -R a+rX "$F"
fi

if [ "$OWN" != no ]; then
# --- Blob container (issue #41): rag/ and the worker's spool, owned by the services' uid ----------------------------
mkdir -p /blobs/rag /blobs/spool
chown 1654:1654 /blobs/rag /blobs/spool
chmod 0750 /blobs/rag /blobs/spool

chown -R 999:999 "$C/postgres" "$S/postgres"
[ ! -d "$S/pgbackrest" ] || chown -R 999:999 "$S/pgbackrest"
chown -R 10001:10001 "$C/parser"
chown -R 10002:10002 "$S/renderer"
chown -R 1000:1000 "$S/openwebui" /openwebui-data
chmod 0700 "$S/openwebui"
# Written by rag-openwebui-setup (Open WebUI's uid 1000), read by rag-api (group 1654; setgid keeps the group).
chown 1000:1654 "$S/openwebui-feedback"
chmod 2750 "$S/openwebui-feedback"
chown -R 1654:1654 "$C/api" "$C/admin" "$C/worker" "$C/migrations" "$S/api" "$S/admin" "$S/worker" "$S/migrations" "$S/keys"
chmod 755 "$C" "$S"
# The pgBackRest repository and the upload copy where they are mounted (Compose's compose.backup.yaml): the directory
# itself only, since a repository holds many files. A share that refuses root's chown is named on the checklist.
for d in pgbackrest:999 uploads:1654; do
  [ -d "/backup/${d%%:*}" ] || continue
  chown "${d#*:}:${d#*:}" "/backup/${d%%:*}" 2>/dev/null || backup_todo="${backup_todo:-} /backup/${d%%:*}"
done
fi
echo "rag-bootstrap: certificates and secrets ready"

# --- What is still missing: a checklist at the end of the log (never fatal: the stack starts and waits) ------------
todo=""
note() { todo="$todo
  - $1"; }
example() { case "$1" in ""|*example.se*|*CHANGE*) return 0 ;; esac; return 1; }
for svc in api admin worker; do
  up="$(echo "$svc" | tr '[:lower:]' '[:upper:]')"
  [ -e "$S/$svc/Gateway__Key" ] || note "UME_RAG_${up}_GATEWAY_KEY: no key for rag-$svc. Leave UME_INTEGRATION_MANIFEST unset (the stack file's default) so the gateway creates it, or issue it in the gateway's admin (integration Ume.Rag) and set the variable; then update the stack, no restart needed"
done
# A name under UME_DOMAIN is named once, as the domain; a name set of its own, as its variable.
if [ -n "${UME_DOMAIN:-}" ] && example "$UME_DOMAIN"; then note "UME_DOMAIN is still an example value (the host names default under it)"; fi
case "$UME_RAG_API_HOST" in "rag.${UME_DOMAIN:-}") ;; *) example "$UME_RAG_API_HOST" && note "UME_RAG_API_HOST is still an example value" ;; esac
# Optional: unset, rag-api's public URL is https://<UME_RAG_API_HOST>.
case "${UME_RAG_API_PUBLIC_URL:-}" in "") ;;
  https://*example.se*|https://*CHANGE*) note "UME_RAG_API_PUBLIC_URL is still an example value" ;;
  https://*) ;; *) note "UME_RAG_API_PUBLIC_URL must be the https URL users reach rag-api at (citation links use it)" ;; esac
# Optional (identity design §19): unset, rag-admin's client secret is the setup wizard's; only a placeholder is noted,
# since any value declares the sign-in and keeps the wizard closed.
case "${UME_RAG_OIDC_CLIENT_SECRET:-}" in *example.se*|*CHANGE*)
  note "UME_RAG_OIDC_CLIENT_SECRET is still an example value: set the real secret, or remove it for the setup wizard" ;; esac
case "${UME_RAG_OIDC_REFUSE_GUESTS:-}" in ""|true|false) ;;
  *) note "UME_RAG_OIDC_REFUSE_GUESTS must be true or false (or unset for the setup wizard's choice); it is left out" ;; esac
# Certificates (#705): rag-postgres and rag-parser read theirs only at start, and a stack update recreates neither when
# its configuration is unchanged (Kestrel watches rag-api's, rag-admin's and rag-worker's files). Each certificate that expires within
# 60 days is named on every run; the service certificates are re-issued below 30 days, the CA never.
restart=""
for svc in $reissued; do case "$svc" in postgres|parser) restart="$restart rag-$svc" ;; esac; done
[ -z "$restart" ] || note "restart$restart (Containers, select, Restart): a re-issued certificate takes effect there only after a restart"
for crt in "$C/ca.pem" "$C"/*/*.crt; do
  quiet openssl x509 -checkend 5184000 -noout -in "$crt" && continue
  end="$(openssl x509 -enddate -noout -in "$crt" | cut -d= -f2)"
  case "$crt" in
    "$C/ca.pem") note "RAG's internal CA expires $end, within 60 days: replace it (docs/portainer.md, Internal certificates)" ;;
    *) note "the certificate of rag-$(basename "$(dirname "$crt")") expires $end, within 60 days: update the stack once fewer than 30 days remain, and rag-bootstrap re-issues it" ;;
  esac
done
# Retired variables (collected above): tidying, nothing depends on it.
old_ifs="$IFS"; IFS=','
for name in $retired; do note "$name is set and no longer read ($(retired_hint "$name")): remove it from the stack's variables"; done
IFS="$old_ifs"
# The TLS proxy's host names (collected while its nginx.conf was written, above).
old_ifs="$IFS"; IFS='|'
for item in $proxy_todo; do [ -z "$item" ] || note "$item"; done
IFS="$old_ifs"
[ -z "${backup_todo:-}" ] || note "could not give${backup_todo} to its user (uid 999 for pgbackrest, 1654 for uploads): chown it on the backup share"
case ",${COMPOSE_PROFILES:-}," in
  *,openwebui,*)
    case "${UME_RAG_OPENWEBUI_HOST:-}" in "chat.${UME_DOMAIN:-}") ;;
      *) example "${UME_RAG_OPENWEBUI_HOST:-}" && note "UME_RAG_OPENWEBUI_HOST: the chat's public host name (or set UME_DOMAIN)" ;; esac
    # Optional (identity design §19): unset, Open WebUI's client secret is the setup wizard's; only a placeholder is noted.
    case "${UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET:-}" in *example.se*|*CHANGE*)
      note "UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET is still an example value: set the real secret, or empty it for the setup wizard" ;; esac ;;
esac
if [ -n "$todo" ]; then
  echo "rag-bootstrap: still to do (docs/portainer.md):$todo"
else
  echo "rag-bootstrap: every setting it checks is in place"
fi
# Not settings to check here: rag-bootstrap has no network and can't see whether setup is done, so the sign-in line is
# printed on every run; who is a platform admin comes from the sign-in token.
if [ -n "${UME_RAG_OIDC_AUTHORITY:-}" ] && [ -s "$S/admin/Oidc__ClientSecret" ]; then
  echo "rag-bootstrap: sign-in to Ume.Rag's admin is declared by the stack's variables (the authority and UME_RAG_OIDC_CLIENT_SECRET), so the setup wizard stays closed"
else
  echo "rag-bootstrap: sign-in is set in the setup wizard: https://$ADMIN_HOST/setup, with the setup code in rag-admin's log (first start, or after UME_RAG_SETUP_REOPEN); skip this if you can already sign in"
fi
echo "rag-bootstrap: the first platform admin needs the role rag-platform-admin in the token's roles claim, or a group named in UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN; the setup wizard's test sign-in says whether you have it"
