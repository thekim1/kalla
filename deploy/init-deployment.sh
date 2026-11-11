#!/usr/bin/env bash
# First-run setup for the production Compose files (plans/platform/repo-and-deployment.md §6, §7.1): generates an
# internal CA, service certificates, the Data Protection certificate, the database passwords, the key pepper, the api's
# keys, the worker's crawl key pepper, the renderer's WebSocket path, the blob master key, the pgBackRest repository
# settings and deploy/.env. Safe to
# re-run: existing secrets are never overwritten (a new pepper, key, passphrase or Data Protection certificate would
# break existing data or backups); connection strings and other derived files are written again.
#
#   ./deploy/init-deployment.sh                      # prompts for what it needs
#   ./deploy/init-deployment.sh --oidc-authority https://<keycloak host>/realms/ume \
#       --oidc-client-secret-file ume-rag-admin.secret \
#       --api-host rag-api.intern.umea.se --admin-host rag-admin.intern.umea.se \
#       --gateway-url https://<gateway host> --gateway-key-file api:Eu=api-eu.key \
#       --gateway-key-file admin:Eu=admin-eu.key --gateway-key-file worker:Eu=worker-eu.key \
#       --openwebui-host chat.intern.umea.se --openwebui-oidc-client-secret-file open-webui.secret \
#       --backup-target /mnt/backup/ume-rag --allow-file-keys --yes
#   ./deploy/init-deployment.sh --renew-certs        # re-issue internal certificates only
#   ./deploy/init-deployment.sh --renew-ca           # new internal CA, then re-issue its certificates
#
# Sign-in: P1 uses the Keycloak shared with the gateway (realm ume, RAG's clients from deploy/keycloak/). P2 (Entra):
# --oidc-authority https://login.microsoftonline.com/<tenant>/v2.0 with the registrations ume-rag-admin and
# ume-rag-open-webui (--openwebui-oidc-client-id).
#
# Options (defaults in brackets):
#   --oidc-authority URL, --oidc-client-id ID [ume-rag-admin], --oidc-client-secret-file FILE
#                           the admin BFF's own OIDC client (never the gateway's): P1 the shared Keycloak's realm ume
#   --api-host HOST, --admin-host HOST, --api-port N [8543], --admin-port N [9543], --bind ADDR [127.0.0.1]
#   --gateway-url URL       the LLM gateway (HTTPS); --ca-file FILE adds its CA (and your PKI chain) to the services'
#                           trust store
#   --gateway-key-file [SERVICE:]LEVEL=FILE
#                           a gateway key per level (OnPrem, Eu, External; repeat the option): without SERVICE api and
#                           admin get it, and the worker too unless it is External; SERVICE (api, admin, worker) gives
#                           one deployable its own key and wins over the shared one. The worker never holds External.
#                           P1a calls every model at Eu.
#   --org-sync-url URL      the gateway's Data API (HTTPS; its own service and often its own host, without /v1): the admin's
#                           org sync and model catalogue sync read /v1/catalog/* there; both stay off without it
#   --org-sync-key-file FILE  the gateway Data API key for the admin's org sync (catalog.read only)
#   --key-provider file [file] --allow-file-keys
#                           the file key provider (pilot and test hosts, postponed decision K1) needs --allow-file-keys
#   --backup-target DIR|s3://ENDPOINT/BUCKET[/PATH]|local
#                           pgBackRest repo1 and the upload copy: the backup share mounted on this host (DIR/pgbackrest
#                           and DIR/uploads), an S3-compatible store (with --backup-s3-credentials-file FILE: access key
#                           and secret key on two lines, and --backup-s3-region R [us-east-1]; the upload copy to S3 is
#                           not built yet), or `local` (a Docker volume and deploy/upload-backup: test hosts only)
#   --crawl-allow CIDR[,CIDR]  private ranges the crawler may fetch (municipal hosts)
#   --host-memory 16|24 [16]  the host profile of §6.2 (24 adds compose.host-24g.yaml and the ClamAV 24 GB confs)
#   --freshclam-proxy HOST:PORT  freshclam reaches database.clamav.net through this HTTP proxy
#   --openwebui-host HOST   the optional Open WebUI stack (compose.openwebui.yaml): its secrets, settings and store, and
#                           the feedback service account (deploy/openwebui-feedback-key.sh)
#     --openwebui-oidc-client-id ID [open-webui], --openwebui-oidc-client-secret-file FILE,
#     --openwebui-oidc-provider-url URL [the OIDC authority's /.well-known/openid-configuration],
#     --openwebui-oidc-provider-name NAME [Umeå kommun]
#                           Open WebUI's own OIDC client (P1: Keycloak client open-webui; P2: Entra ume-rag-open-webui)
#                           and the name on its sign-in button
#     --openwebui-api-key-file FILE, --openwebui-rag-client-id ID
#                           the ume-rag-sk- key and the id of Open WebUI's client in rag-api's registry (created in the
#                           admin API once the stack runs; rag-api's OpenWebUi:ClientId)
#     --openwebui-data-dir DIR [deploy/openwebui-data]
#   --role all [all]        multi-host roles (data, ingestion, ...) come with P2
#   --proxy                 a reverse proxy terminates public TLS and re-encrypts to this host; needs distinct
#                           hostnames and binds the listeners to 0.0.0.0 (firewall them to the proxy)
#   --upstream-host HOST    address the proxy uses to reach this host [hostname]
#   --proxy-address ADDR[,ADDR]
#                           the reverse proxy's source addresses (IPv4 or CIDR): the host firewall then accepts new
#                           connections to the published ports (rag-api, rag-admin, Open WebUI) only from these (#453)
#   --rolling               a second rag-api and rag-admin (compose.rolling.yaml, on the next ports up) for updates
#                           without downtime with deploy/rolling-update.sh (docs/zero-downtime-updates.md); with
#                           --proxy the nginx configuration lists both slots
#   --api-cert/--api-key/--admin-cert/--admin-key FILE
#                           certificates from your PKI for the public listeners instead of the internal CA
#   --certs-dir DIR [deploy/certs], --secrets-dir DIR [deploy/secrets]
#   --no-chown              leave the files owned by you
#   --yes                   no prompts: every value comes from an option or its default
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/certs"; secrets="$here/secrets"; env_file="$here/.env"
oidc_authority=""; oidc_client="ume-rag-admin"; oidc_secret_file=""; api_host=""; admin_host=""
api_port=8543; admin_port=9543; bind=""; gateway_url=""; ca_file=""; org_sync_file=""; org_sync_url=""
key_provider="file"; allow_file_keys=0; backup_target=""; s3_credentials=""; s3_region="us-east-1"
crawl_allow=""; host_memory=16; freshclam_proxy=""; role="all"
owui_host=""; owui_client="open-webui"; owui_secret_file=""; owui_provider=""; owui_provider_name="Umeå kommun"
owui_key_file=""; owui_rag_client=""; owui_data=""
proxy=0; upstream_host=""; proxy_sources=""; api_cert=""; api_key=""; adm_cert=""; adm_key=""
yes=0; renew=0; renew_ca=0; chown=1; rolling=0
gateway_keys=()

# The comment block at the top of this file.
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --oidc-authority) oidc_authority="$2"; shift 2;;
    --oidc-client-id) oidc_client="$2"; shift 2;;
    --oidc-client-secret-file) oidc_secret_file="$2"; shift 2;;
    --api-host) api_host="$2"; shift 2;;
    --admin-host) admin_host="$2"; shift 2;;
    --api-port) api_port="$2"; shift 2;;
    --admin-port) admin_port="$2"; shift 2;;
    --bind) bind="$2"; shift 2;;
    --gateway-url) gateway_url="$2"; shift 2;;
    --ca-file) ca_file="$2"; shift 2;;
    --gateway-key-file) gateway_keys+=("$2"); shift 2;;
    --org-sync-url) org_sync_url="$2"; shift 2;;
    --org-sync-key-file) org_sync_file="$2"; shift 2;;
    --key-provider) key_provider="$2"; shift 2;;
    --allow-file-keys) allow_file_keys=1; shift;;
    --backup-target) backup_target="$2"; shift 2;;
    --backup-s3-credentials-file) s3_credentials="$2"; shift 2;;
    --backup-s3-region) s3_region="$2"; shift 2;;
    --crawl-allow) crawl_allow="$2"; shift 2;;
    --host-memory) host_memory="$2"; shift 2;;
    --freshclam-proxy) freshclam_proxy="$2"; shift 2;;
    --openwebui-host) owui_host="$2"; shift 2;;
    --openwebui-oidc-client-id) owui_client="$2"; shift 2;;
    --openwebui-oidc-client-secret-file) owui_secret_file="$2"; shift 2;;
    --openwebui-oidc-provider-url) owui_provider="$2"; shift 2;;
    --openwebui-oidc-provider-name) owui_provider_name="$2"; shift 2;;
    --openwebui-api-key-file) owui_key_file="$2"; shift 2;;
    --openwebui-rag-client-id) owui_rag_client="$2"; shift 2;;
    --openwebui-data-dir) owui_data="$2"; shift 2;;
    --role) role="$2"; shift 2;;
    --proxy) proxy=1; shift;;
    --rolling) rolling=1; shift;;
    --upstream-host) upstream_host="$2"; shift 2;;
    --proxy-address) proxy_sources="$2"; shift 2;;
    --api-cert) api_cert="$2"; shift 2;;
    --api-key) api_key="$2"; shift 2;;
    --admin-cert) adm_cert="$2"; shift 2;;
    --admin-key) adm_key="$2"; shift 2;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    --no-chown) chown=0; shift;;
    --renew-certs) renew=1; shift;;
    --renew-ca) renew=1; renew_ca=1; shift;;
    --yes|-y) yes=1; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
command -v openssl >/dev/null || die "openssl is required"
[ "$role" = all ] || die "--role $role: multi-host roles come with P2 (repo-and-deployment §6.4); use --role all"
case "$host_memory" in 16|24) ;; *) die "--host-memory must be 16 or 24";; esac
case "$key_provider" in
  file) ;;
  *) die "--key-provider $key_provider: only the file key provider exists until postponed decision K1 is made";;
esac
case "$freshclam_proxy" in ""|*:[0-9]*) ;; *) die "--freshclam-proxy must be HOST:PORT";; esac
case "$org_sync_url" in ""|https://*) ;; *) die "--org-sync-url must be an https:// URL";; esac
for a in ${proxy_sources//,/ }; do
  [[ "$a" =~ ^[0-9]+(\.[0-9]+){3}(/[0-9]+)?$ ]] || die "--proxy-address takes IPv4 addresses or CIDRs, comma-separated: $a"
done
# The clients append v1/catalog/...: keep the bare root.
org_sync_url="${org_sync_url%/}"; org_sync_url="${org_sync_url%/v1}"

ask() { # ask VAR "prompt" [default]
  local var="$1" prompt="$2" default="${3:-}" reply
  [ -n "${!var}" ] && return
  if [ "$yes" = 1 ] || [ ! -t 0 ]; then
    [ -n "$default" ] || die "$prompt is required (pass the matching option)"
    printf -v "$var" '%s' "$default"; return
  fi
  read -r -p "$prompt${default:+ [$default]}: " reply
  reply="${reply:-$default}"; [ -n "$reply" ] || die "$prompt is required"
  printf -v "$var" '%s' "$reply"
}

if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  echo "Ume Rag production setup"
  [ "$allow_file_keys" = 1 ] || die "the file key provider keeps the blob master key in a file on this host; pass \
--allow-file-keys to accept that (pilot and test hosts, postponed decision K1)"
  ask oidc_authority "OIDC authority URL (https://...)"
  case "$oidc_authority" in https://*) ;; *) die "OIDC authority must be an https:// URL";; esac
  ask gateway_url "LLM gateway URL (https://...)"
  case "$gateway_url" in https://*) ;; *) die "the gateway URL must be an https:// URL";; esac
  gateway_url="${gateway_url%/}"
  ask api_host "Public hostname for rag-api" "$(hostname -f 2>/dev/null || hostname)"
  if [ "$proxy" = 1 ]; then
    ask admin_host "Public hostname for the admin UI (must differ from rag-api's)"
    [ "$admin_host" != "$api_host" ] || die "proxy mode needs two distinct hostnames (the proxy routes by name)"
    ask upstream_host "Address the proxy uses to reach this host" "$(hostname -f 2>/dev/null || hostname)"
  else
    ask admin_host "Public hostname for the admin UI" "$api_host"
  fi
  ask backup_target "pgBackRest repository (a mounted share's path, s3://endpoint/bucket[/path], or local)"
fi
for f in "$oidc_secret_file" "$org_sync_file" "$ca_file" "$s3_credentials" "$owui_secret_file" "$owui_key_file"; do
  [ -z "$f" ] || [ -r "$f" ] || die "cannot read $f"
done
for spec in ${gateway_keys[@]+"${gateway_keys[@]}"}; do
  target="${spec%%=*}"; level="${target#*:}"; svc=""; [ "$target" = "$level" ] || svc="${target%%:*}"
  case "$svc" in ""|api|admin|worker) ;; *) die "--gateway-key-file SERVICE:LEVEL=FILE: SERVICE is api, admin or worker";; esac
  case "$level" in OnPrem|Eu|External) ;; *) die "--gateway-key-file [SERVICE:]LEVEL=FILE: LEVEL is OnPrem, Eu or External";; esac
  [ "$svc:$level" != worker:External ] || die "the worker never holds the External key (simplification pass §6)"
  [ "$spec" != "$target" ] || die "--gateway-key-file takes [SERVICE:]LEVEL=FILE, not $spec"
  [ -r "${spec#*=}" ] || die "cannot read ${spec#*=}"
done
if [ -n "$owui_host" ]; then
  case "$owui_provider_name" in ""|*"'"*|*$'\n'*) die "--openwebui-oidc-provider-name: a non-empty name without ' or a line break";; esac
  [ -n "$owui_client" ] || die "--openwebui-oidc-client-id must not be empty"
  case "$owui_provider" in ""|https://*) ;; *) die "--openwebui-oidc-provider-url must be an https:// URL";; esac
fi
pki=0
if [ -n "$api_cert$api_key$adm_cert$adm_key" ]; then
  pki=1
  for f in "$api_cert" "$api_key" "$adm_cert" "$adm_key"; do
    if [ -z "$f" ] || [ ! -r "$f" ]; then die "PKI mode needs readable --api-cert, --api-key, --admin-cert and --admin-key"; fi
  done
fi
if [ -z "$bind" ]; then bind="127.0.0.1"; [ "$proxy" = 1 ] && bind="0.0.0.0"; fi
if [ "$proxy" = 1 ]; then public_api=""; public_admin=""; else public_api=":$api_port"; public_admin=":$admin_port"; fi

new_file() { # new_file PATH — true (and creates parent) if PATH does not exist yet
  [ -e "$1" ] && return 1
  mkdir -p "$(dirname "$1")"; return 0
}
rand() { openssl rand -hex 32; }

mkdir -p "$certs" "$secrets"
# After the first run the files belong to the containers' users: a file this script cannot see would look missing and
# be generated again, so stop instead.
for d in "$certs"/*/ "$secrets"/*/; do
  if [ -d "$d" ] && { [ ! -r "$d" ] || [ ! -x "$d" ]; }; then
    die "cannot read $d: run as root (or with sudo) to re-run on this deployment"
  fi
done

# --- Internal CA (trusted only by the services themselves) ----------------------
# keyUsage makes it RFC 5280-conformant; strict clients (Python 3.13, newer Go and Java) refuse a CA without it.
if [ "$renew_ca" = 1 ] && [ -e "$certs/ca.pem" ]; then
  mv "$certs/ca.pem" "$certs/ca.pem.old"; rm -f "$certs/ca.key"
fi
if new_file "$certs/ca.pem"; then
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$certs/ca.key" -out "$certs/ca.pem" \
    -days 3650 -subj "/CN=ume-rag-internal-ca" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" >/dev/null 2>&1
fi

# --- Service certificates ---------------------------------------------------------
issue() { # issue DIR SAN... — DIR/DIR.crt and DIR/DIR.key, valid for each DNS name given
  local svc="$1"; shift; local san="DNS:localhost" h tmp
  [ -r "$certs/ca.key" ] || die "$certs/ca.key is needed to issue certificates: copy it back from where you keep it offline"
  for h in "$@"; do [ -n "$h" ] && san="$san,DNS:$h"; done
  mkdir -p "$certs/$svc"; tmp="$(mktemp -d)"
  openssl req -newkey rsa:3072 -nodes -keyout "$certs/$svc/$svc.key" -out "$tmp/leaf.csr" -subj "/CN=$svc" >/dev/null 2>&1
  printf 'subjectAltName=%s\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature,keyEncipherment\nsubjectKeyIdentifier=hash\nauthorityKeyIdentifier=keyid\n' \
    "$san" > "$tmp/leaf.ext"
  openssl x509 -req -in "$tmp/leaf.csr" -CA "$certs/ca.pem" -CAkey "$certs/ca.key" -CAcreateserial \
    -CAserial "$tmp/ca.srl" -out "$certs/$svc/$svc.crt" -days 825 -extfile "$tmp/leaf.ext" >/dev/null 2>&1
  rm -rf "$tmp"; : > "$certs/$svc/.internal"
}
if [ "$renew" = 1 ] && [ -f "$env_file" ]; then
  # shellcheck disable=SC1090
  . "$env_file"
  api_host="${UME_RAG_API_PUBLIC_URL#https://}"; api_host="${api_host%%[:/]*}"
  admin_host="${UME_RAG_ADMIN_PUBLIC_HOST:-$api_host}"
fi
# Internal names only: rag-worker's listener serves its health probe, rag-parser's the worker, postgres every service.
if [ "$renew" = 1 ] || new_file "$certs/postgres/postgres.crt"; then issue postgres postgres; fi
if [ "$renew" = 1 ] || new_file "$certs/worker/worker.crt"; then issue worker rag-worker; fi
if [ "$renew" = 1 ] || new_file "$certs/parser/parser.crt"; then issue parser rag-parser; fi
use_pki() { # use_pki DIR CERT KEY — install operator-supplied PKI files
  mkdir -p "$certs/$1"; cp "$2" "$certs/$1/$1.crt"; cp "$3" "$certs/$1/$1.key"; rm -f "$certs/$1/.internal"
}
if [ "$pki" = 1 ]; then
  use_pki api "$api_cert" "$api_key"; use_pki admin "$adm_cert" "$adm_key"
else
  # On renewal only re-issue what this script issued (PKI-supplied certificates have no marker).
  if { [ "$renew" = 1 ] && [ -e "$certs/api/.internal" ]; } || new_file "$certs/api/api.crt"; then issue api rag-api "$api_host"; fi
  if { [ "$renew" = 1 ] && [ -e "$certs/admin/.internal" ]; } || new_file "$certs/admin/admin.crt"; then issue admin rag-admin "$admin_host"; fi
fi

# --- Data Protection certificate (stable: never regenerate once data exists) --------
dp="$certs/data-protection.pfx"
if new_file "$dp"; then
  tmp="$(mktemp -d)"
  openssl req -x509 -newkey rsa:3072 -nodes -keyout "$tmp/dp.key" -out "$tmp/dp.crt" -days 3650 \
    -subj "/CN=ume-rag-data-protection" >/dev/null 2>&1
  openssl pkcs12 -export -inkey "$tmp/dp.key" -in "$tmp/dp.crt" -out "$dp" -passout pass: >/dev/null 2>&1
  rm -rf "$tmp"
fi
for svc in api admin migrations; do
  mkdir -p "$certs/$svc"
  cp "$dp" "$certs/$svc/data-protection.pfx"
done
for svc in postgres api admin worker parser migrations; do cp "$certs/ca.pem" "$certs/$svc/ca.pem"; done
# The services that call out (gateway, OIDC authority, the PKI listeners) trust the internal CA plus, when given, the
# gateway's CA and your PKI chain: SSL_CERT_FILE is their trust store.
if [ -n "$ca_file" ]; then
  for svc in api admin worker; do cat "$ca_file" >> "$certs/$svc/ca.pem"; done
fi

# --- Secrets ----------------------------------------------------------------------
mkdir -p "$secrets"/{postgres,pgbackrest,migrations,api,admin,worker,keys}
for role in bootstrap migrator api admin worker; do
  new_file "$secrets/postgres/${role}_password" && rand > "$secrets/postgres/${role}_password"
done
new_file "$secrets/.pepper" && openssl rand -hex 48 > "$secrets/.pepper"
connection() { # connection ROLE POOL — ragdb over TLS, verified against the internal CA
  printf 'Host=postgres;Database=ragdb;Username=ume_rag_%s;Password=%s;SSL Mode=VerifyFull;Root Certificate=/run/certs/ca.pem;GSS Encryption Mode=Disable;Maximum Pool Size=%s' \
    "$1" "$(tr -d '\r\n' < "$secrets/postgres/${1}_password")" "$2"
}
# Pools per instance (data-model §1.1): api 12, admin 5, worker 10, migrator 2.
connection migrator 2 > "$secrets/migrations/ConnectionStrings__ragdb"
connection api 12 > "$secrets/api/ConnectionStrings__ragdb"
connection admin 5 > "$secrets/admin/ConnectionStrings__ragdb"
connection worker 10 > "$secrets/worker/ConnectionStrings__ragdb"
for svc in migrations api admin; do tr -d '\n' < "$secrets/.pepper" > "$secrets/$svc/Security__KeyPepper"; done

# The api's own keys (knowledge design §4.6, §5.4; chat design §11.1); a rotation adds a new kid next to k1.
new_file "$secrets/api/Security__DownloadGrantKeys__k1" && rand > "$secrets/api/Security__DownloadGrantKeys__k1"
new_file "$secrets/api/Security__CacheKeyPepper" && rand > "$secrets/api/Security__CacheKeyPepper"
new_file "$secrets/api/Security__ConversationIdKeys__k1" && rand > "$secrets/api/Security__ConversationIdKeys__k1"
# Shared with Open WebUI (FORWARD_USER_INFO_HEADER_JWT_SECRET), which signs the user JWT with it (identity design §5.2).
new_file "$secrets/.openwebui-jwt-secret" && rand > "$secrets/.openwebui-jwt-secret"
tr -d '\n' < "$secrets/.openwebui-jwt-secret" > "$secrets/api/OpenWebUi__JwtSecret"
# The worker's HMAC key for host names (HostHash of security event 5101, docs/security-events.md; from P2 also the Redis
# crawl leases, performance pass §3.6): 32 random bytes, hex.
if new_file "$secrets/worker/Security__CrawlKeyPepper"; then
  openssl rand -hex 32 | tr -d '\n' > "$secrets/worker/Security__CrawlKeyPepper"
fi
# The renderer's secret WebSocket path (connectors design §8.3): a page's own WebSocket could otherwise reach
# launch-server's port. The worker gets the whole endpoint as a secret file; the renderer reads the path only from its
# environment, so renderer.env is read by `docker compose` and stays owned by you. Delete .render-ws-path to rotate it.
new_file "$secrets/.render-ws-path" && rand > "$secrets/.render-ws-path"
render_path="/$(tr -d '\r\n' < "$secrets/.render-ws-path")"
printf 'ws://rag-renderer:3000%s' "$render_path" > "$secrets/worker/Render__Endpoint"
printf 'UME_RAG_RENDER_WS_PATH=%s\n' "$render_path" > "$secrets/renderer.env"

# Shared keys first, then each deployable's own (the gateway team issues one key per deployable), which win.
for pass in shared own; do
  for spec in ${gateway_keys[@]+"${gateway_keys[@]}"}; do
    target="${spec%%=*}"; level="${target#*:}"
    if [ "$target" = "$level" ]; then
      [ "$pass" = shared ] || continue
      services=(api admin worker)
    else
      [ "$pass" = own ] || continue
      services=("${target%%:*}")
    fi
    for svc in "${services[@]}"; do
      # The worker never holds the External key (simplification pass §6).
      [ "$svc" = worker ] && [ "$level" = External ] && continue
      tr -d '\r\n' < "${spec#*=}" > "$secrets/$svc/Gateway__Keys__$level"
    done
  done
done
if [ -n "$oidc_secret_file" ]; then tr -d '\r\n' < "$oidc_secret_file" > "$secrets/admin/Oidc__ClientSecret"; fi
if [ -n "$org_sync_file" ]; then tr -d '\r\n' < "$org_sync_file" > "$secrets/admin/OrgSync__DataApiKey"; fi

# Blob master key of the file key provider (plan §8a): 32 random bytes, base64. Escrowed by escrow-keys.sh; without it
# the `internal` blobs cannot be read.
if new_file "$secrets/keys/rag-blob-internal"; then
  openssl rand -base64 32 > "$secrets/keys/rag-blob-internal"
  chmod 0400 "$secrets/keys/rag-blob-internal"
fi

# pgBackRest repository (§7.4). The passphrase is generated once and escrowed; repo.conf is rewritten from it.
new_file "$secrets/pgbackrest/cipher-pass" && openssl rand -base64 48 | tr -d '\n' > "$secrets/pgbackrest/cipher-pass"
backup_dir=""; upload_dir=""; backup_endpoint=""
if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  {
    echo "# Written by init-deployment.sh (repo-and-deployment §7.4); the passphrase is pgbackrest/cipher-pass."
    echo "[global]"
    echo "repo1-cipher-type=aes-256-cbc"
    echo "repo1-cipher-pass=$(cat "$secrets/pgbackrest/cipher-pass")"
    case "$backup_target" in
      s3://*)
        [ -n "$s3_credentials" ] || die "an s3:// backup target needs --backup-s3-credentials-file"
        location="${backup_target#s3://}"; endpoint="${location%%/*}"; bucket_path="${location#*/}"
        if [ "$bucket_path" = "$location" ] || [ -z "$bucket_path" ]; then die "--backup-target s3://ENDPOINT/BUCKET[/PATH]"; fi
        bucket="${bucket_path%%/*}"; path="${bucket_path#"$bucket"}"; path="${path#/}"; path="${path%/}"
        backup_endpoint="$endpoint"
        echo "repo1-type=s3"
        echo "repo1-path=/${path:-ume-rag}"
        echo "repo1-s3-endpoint=$endpoint"
        echo "repo1-s3-bucket=$bucket"
        echo "repo1-s3-region=$s3_region"
        echo "repo1-s3-key=$(sed -n 1p "$s3_credentials" | tr -d '\r')"
        echo "repo1-s3-key-secret=$(sed -n 2p "$s3_credentials" | tr -d '\r')"
        echo "repo1-s3-uri-style=path"
        ;;
      local)
        echo "repo1-type=posix"
        echo "repo1-path=/var/lib/pgbackrest"
        upload_dir="$here/upload-backup"
        ;;
      /*)
        echo "repo1-type=posix"
        echo "repo1-path=/var/lib/pgbackrest"
        backup_target="${backup_target%/}"; backup_dir="$backup_target/pgbackrest"; upload_dir="$backup_target/uploads"
        ;;
      *) die "--backup-target is an absolute path, s3://ENDPOINT/BUCKET[/PATH] or local";;
    esac
  } > "$secrets/pgbackrest/repo.conf"
  for d in "$backup_dir" "$upload_dir"; do [ -z "$d" ] || mkdir -p "$d"; done
fi

# --- ClamAV confs (compose.clamav-conf.yaml): copies of the image's, written only when a setting differs -------------
# Left as they are by --renew-certs.
clamav_conf=0
if [ "$renew" = 1 ] && [ -f "$env_file" ]; then
  :
elif [ "$host_memory" = 24 ] || [ -n "$freshclam_proxy" ]; then
  clamav_conf=1
  src=""
  for d in "$here/clamav" "$here/../images/clamav"; do
    [ -r "$d/clamd.conf" ] && [ -r "$d/freshclam.conf" ] && { src="$d"; break; }
  done
  [ -n "$src" ] || die "the ClamAV image's clamd.conf and freshclam.conf are needed (deploy/clamav/ in the release \
bundle, images/clamav/ in the repository)"
  mkdir -p "$secrets/clamav"
  cp "$src/clamd.conf" "$secrets/clamav/clamd.conf"
  cp "$src/freshclam.conf" "$secrets/clamav/freshclam.conf"
  if [ "$host_memory" = 24 ]; then
    # 24 GB host (mem_limit 3g, §5.5): keep the old engine during a reload, and test-load updates before using them.
    sed -i 's/^ConcurrentDatabaseReload no$/ConcurrentDatabaseReload yes/' "$secrets/clamav/clamd.conf"
    sed -i 's/^TestDatabases no$/TestDatabases yes/' "$secrets/clamav/freshclam.conf"
    if ! grep -qx 'ConcurrentDatabaseReload yes' "$secrets/clamav/clamd.conf" \
      || ! grep -qx 'TestDatabases yes' "$secrets/clamav/freshclam.conf"; then
      die "the ClamAV confs in $src no longer have the lines this script changes"
    fi
  fi
  if [ -n "$freshclam_proxy" ]; then
    printf '\n# Egress through a proxy (init-deployment.sh --freshclam-proxy).\nHTTPProxyServer %s\nHTTPProxyPort %s\n' \
      "${freshclam_proxy%:*}" "${freshclam_proxy##*:}" >> "$secrets/clamav/freshclam.conf"
  fi
else
  rm -rf "$secrets/clamav"
fi

# --- Open WebUI (optional, compose.openwebui.yaml) ---------------------------------
# set_env_line FILE KEY VALUE — KEY='VALUE' in FILE, replacing an existing KEY= line (single quotes: Compose takes the
# value literally, without interpolating $).
set_env_line() {
  local tmp
  case "$3" in ""|*"'"*|*$'\n'*) die "the value for $2 is empty or contains ' or a line break";; esac
  tmp="$(mktemp "$1.XXXXXX")"
  K="$2" V="$3" awk 'BEGIN { line = ENVIRON["K"] "=\047" ENVIRON["V"] "\047" }
    index($0, ENVIRON["K"] "=") == 1 { if (!done) print line; done = 1; next }
    { print }
    END { if (!done) print line }' "$1" > "$tmp"
  mv "$tmp" "$1"
}
if [ -n "$owui_host" ]; then
  mkdir -p "$secrets/openwebui"
  owui_data="${owui_data:-$here/openwebui-data}"; mkdir -p "$owui_data"
  new_file "$secrets/openwebui/webui_secret_key" && rand > "$secrets/openwebui/webui_secret_key"
  # Read by `docker compose` (Open WebUI takes these only from its environment), so it stays owned by you. Written once;
  # a re-run changes only the values passed as options, never the JWT secret.
  if new_file "$secrets/openwebui.env"; then
    cat > "$secrets/openwebui.env" <<EOF
FORWARD_USER_INFO_HEADER_JWT_SECRET=$(cat "$secrets/.openwebui-jwt-secret")
# The ume-rag-sk- connection key of Open WebUI's client in rag-api (issued in the admin API; --openwebui-api-key-file).
OPENAI_API_KEYS=
# The secret of Open WebUI's own OIDC client (P1: Keycloak client open-webui; --openwebui-oidc-client-secret-file).
OAUTH_CLIENT_SECRET=
EOF
  fi
  # The feedback service account (chat design §14.2): Open WebUI creates it as its first user, and so its only admin,
  # when it starts on an empty store. Generated once; openwebui-feedback-key.sh signs in with it to mint its API key.
  if ! grep -q '^WEBUI_ADMIN_EMAIL=' "$secrets/openwebui.env"; then
    printf '# The feedback service account (deploy/openwebui-feedback-key.sh); never a person.\n' >> "$secrets/openwebui.env"
    set_env_line "$secrets/openwebui.env" WEBUI_ADMIN_EMAIL feedback-import@localhost
    set_env_line "$secrets/openwebui.env" WEBUI_ADMIN_NAME "Feedback import"
    set_env_line "$secrets/openwebui.env" WEBUI_ADMIN_PASSWORD "$(rand)"
  fi
  if [ -n "$owui_key_file" ]; then
    set_env_line "$secrets/openwebui.env" OPENAI_API_KEYS "$(tr -d '\r\n' < "$owui_key_file")"
  fi
  if [ -n "$owui_secret_file" ]; then
    set_env_line "$secrets/openwebui.env" OAUTH_CLIENT_SECRET "$(tr -d '\r\n' < "$owui_secret_file")"
  fi
fi

# --- Ownership: container UIDs must be able to read their own files -------------------
own() { # own UID PATH... (needs root; falls back to a printed command)
  local id="$1"; shift
  [ "$chown" = 1 ] || return 0
  if [ "$(id -u)" = 0 ]; then chown -R "$id:$id" "$@"; return; fi
  if command -v sudo >/dev/null && sudo -n chown -R "$id:$id" "$@" 2>/dev/null; then return; fi
  chown_todo+=("chown -R $id:$id $*")
}
chown_todo=()
own 999 "$certs/postgres" "$secrets/postgres" "$secrets/pgbackrest"
[ -z "$backup_dir" ] || own 999 "$backup_dir"
[ -z "$upload_dir" ] || own 1654 "$upload_dir"
own 1654 "$certs/api" "$certs/admin" "$certs/worker" "$certs/migrations" \
  "$secrets/api" "$secrets/admin" "$secrets/worker" "$secrets/migrations" "$secrets/keys"
own 10001 "$certs/parser"
[ "$clamav_conf" = 0 ] || own 1000 "$secrets/clamav"
[ -z "$owui_host" ] || own 1000 "$secrets/openwebui" "$owui_data"
chmod 755 "$certs" "$secrets" 2>/dev/null || true

# --- deploy/.env (non-secret settings only) ------------------------------------------
compose_files="$here/compose.prod.yaml:$here/compose.ingestion.yaml"
[ "$host_memory" = 24 ] && compose_files="$compose_files:$here/compose.host-24g.yaml"
[ "$clamav_conf" = 1 ] && compose_files="$compose_files:$here/compose.clamav-conf.yaml"
[ "$rolling" = 1 ] && compose_files="$compose_files:$here/compose.rolling.yaml"
if [ "$renew" = 0 ] || [ ! -f "$env_file" ]; then
  if [ -e "$env_file" ]; then
    mv "$env_file" "$env_file.bak"; echo "Existing .env saved as $(basename "$env_file").bak"
  fi
  cat > "$env_file" <<EOF
# Non-secret settings for the production Compose files. Secrets live in UME_RAG_SECRETS_DIR.
# The files of this deployment: \`docker compose --env-file deploy/.env ...\` uses them without -f.
COMPOSE_FILE=$compose_files
UME_RAG_HOST_MEMORY=$host_memory
UME_RAG_CERTS_DIR=$certs
UME_RAG_SECRETS_DIR=$secrets
UME_RAG_OIDC_AUTHORITY=$oidc_authority
UME_RAG_OIDC_CLIENT_ID=$oidc_client
# Groups that grant a RAG role (';'-separated; each a rag- name or an Entra object id, #413). The same identity provider
# and the same group names can serve the gateway too (#468). Claim names default to roles, groups, departmentCodes and
# teamCodes (UME_RAG_OIDC_ROLE_CLAIM, _GROUP_CLAIM, _DEPARTMENT_CLAIM, _TEAM_CLAIM), as the gateway's UME_OIDC_* do.
# UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN=
# UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN=
# UME_RAG_OIDC_GROUPS_PIPELINE_EDITOR=
# UME_RAG_OIDC_GROUPS_REVIEWER=
# UME_RAG_OIDC_GROUPS_VIEWER=
# With Entra ID directly (P2): UME_RAG_OIDC_TENANT_ID=<tenant id>
UME_RAG_GATEWAY_URL=$gateway_url
# The gateway's Data API (the admin's org sync and model catalogue sync, /v1/catalog/*); empty turns both off.
UME_RAG_ORG_SYNC_URL=$org_sync_url
# Register M2: every model call EU-hosted until on-prem hardware exists; empty once External routes and keys exist.
UME_RAG_GATEWAY_MAX_RESIDENCY=Eu
UME_RAG_API_PUBLIC_URL=https://$api_host$public_api
UME_RAG_ADMIN_PUBLIC_HOST=$admin_host
# Listeners default to loopback (0.0.0.0 with --proxy: the host firewall admits only UME_RAG_PROXY_SOURCES to them).
UME_RAG_PROXY_SOURCES=$proxy_sources
UME_RAG_API_BIND=$bind
UME_RAG_API_PORT=$api_port
UME_RAG_ADMIN_BIND=$bind
UME_RAG_ADMIN_PORT=$admin_port
# The second slots of compose.rolling.yaml (--rolling; unused without it).
UME_RAG_API_B_PORT=$((api_port + 1))
UME_RAG_ADMIN_B_PORT=$((admin_port + 1))
# pgBackRest posix repository on the host (empty: the pgbackrest-repo volume, or unused with S3), the S3 endpoint
# (host firewall), and the upload copy's directory (deploy/backup-uploads.sh).
UME_RAG_BACKUP_DIR=$backup_dir
UME_RAG_BACKUP_ENDPOINT=$backup_endpoint
UME_RAG_UPLOAD_BACKUP_DIR=$upload_dir
UME_RAG_CRAWL_ALLOW=$crawl_allow
# freshclam's HTTP proxy (host firewall: the mirror network reaches only this).
UME_RAG_FRESHCLAM_PROXY=$freshclam_proxy
# UME_RAG_OTEL_ENDPOINT=
# The share of requests traced (default parentbased_traceidratio at 0.1; always_on traces every request):
# UME_RAG_OTEL_TRACES_SAMPLER=parentbased_traceidratio
# UME_RAG_OTEL_TRACES_SAMPLER_ARG=0.1
# Empty where deploy/host/rag-ingestion.slice is not installed (a developer machine):
# UME_RAG_INGESTION_SLICE=
# Pin images by digest once pushed to your registry (the release bundle does this), for example:
# API_IMAGE=ghcr.io/<owner>/ume-rag/api@sha256:...
EOF
  if [ -n "$owui_host" ]; then
    cat >> "$env_file" <<EOF
# Optional Open WebUI stack (compose.openwebui.yaml, a project of its own). Its OIDC client: P1 the shared Keycloak's
# open-webui (realm ume), P2 Entra's ume-rag-open-webui.
UME_RAG_OPENWEBUI_HOST=$owui_host
# rag-api's feedback importer reads Open WebUI here (OpenWebUi:BaseUrl); empty turns the import off.
UME_RAG_OPENWEBUI_BASE_URL=https://$owui_host
UME_RAG_OPENWEBUI_BIND=$bind
UME_RAG_OPENWEBUI_PORT=3000
UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=$owui_client
UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL=${owui_provider:-${oidc_authority%/}/.well-known/openid-configuration}
UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME='$owui_provider_name'
UME_RAG_OPENWEBUI_DATA_DIR=$owui_data
# Open WebUI's client in rag-api's registry (rag-api's OpenWebUi:ClientId; --openwebui-rag-client-id).
UME_RAG_OPENWEBUI_CLIENT_ID=$owui_rag_client
EOF
  fi
fi

own_step="  0. File ownership is set."
[ "$chown" = 1 ] || own_step="  0. Files stay owned by you (--no-chown): the containers' users must be able to read theirs."
if [ ${#chown_todo[@]} -gt 0 ]; then
  own_step="  0. Run as root first (containers run as other users and must read their files):"
  for c in "${chown_todo[@]}"; do own_step="$own_step
       $c"; done
fi
backup_note=""
[ "$backup_target" = local ] && backup_note="
     The backup repository is a Docker volume and the upload copy $here/upload-backup, both on this host
     (--backup-target local): not a backup off the host."
proxy_note=""
if [ "$proxy" = 1 ]; then
  # proxy/nginx.conf from the example, as the gateway's init-deployment.sh writes its own (#468).
  sed -e "s/@API_HOST@/$api_host/g" -e "s/@ADMIN_HOST@/$admin_host/g" -e "s/@UPSTREAM@/$upstream_host/g" \
      -e "s/@API_PORT@/$api_port/g" -e "s/@ADMIN_PORT@/$admin_port/g" \
      -e "s/@API_B_PORT@/$((api_port + 1))/g" -e "s/@ADMIN_B_PORT@/$((admin_port + 1))/g" \
      -e "s/@OPENWEBUI_HOST@/$owui_host/g" -e "s/@OPENWEBUI_PORT@/3000/g" \
      "$here/proxy/nginx.conf.example" > "$here/proxy/nginx.conf"
  [ -z "$owui_host" ] || sed -i 's/^#owui \{0,1\}//' "$here/proxy/nginx.conf"
  [ "$rolling" = 0 ] || sed -i 's/#rolling //' "$here/proxy/nginx.conf"
  proxy_note="
Reverse proxy: $here/proxy/nginx.conf is ready for nginx (add your certificates). For another proxy: forward
https://$api_host to https://$upstream_host:$api_port and https://$admin_host to https://$upstream_host:$admin_port,
preserving the Host header; client_max_body_size 60m and proxy_request_buffering off on
PUT /api/upload-batches/{batchId}/files/{n} of the admin host (admin design §2.2), 2 MB on the api host (chat history,
and on Open WebUI's), 1 MB elsewhere.
Restrict those ports to the proxy with the host firewall."
  [ "$rolling" = 0 ] || proxy_note="$proxy_note
With --rolling, also list the second slots: $upstream_host:$((api_port + 1)) (rag-api) and
$upstream_host:$((admin_port + 1)) (admin), retrying on the other slot; proxy/nginx.conf already does."
  if [ -z "$proxy_sources" ]; then
    proxy_note="$proxy_note
The listeners bind to $bind and nothing on this host limits who reaches them: pass --proxy-address with the
proxy's source addresses (the host firewall then admits only those), or restrict them in a network firewall."
  else
    proxy_note="$proxy_note Installed by deploy/host/install.sh: only $proxy_sources may connect."
  fi
fi
rolling_note=""
if [ "$rolling" = 1 ]; then
  rolling_note="
Updates without downtime: later, ./deploy/rolling-update.sh (docs/zero-downtime-updates.md). The second slots listen
on ports $((api_port + 1)) and $((admin_port + 1))."
  [ "$proxy" = 1 ] || rolling_note="$rolling_note Put a proxy in front that uses both ports of each service."
fi
gateway_note=""
missing=""
# With --renew-certs deploy/.env is kept (and was read above).
sync_url="${org_sync_url:-${UME_RAG_ORG_SYNC_URL:-}}"
if [ -z "$sync_url" ]; then
  gateway_note="
Data API: no --org-sync-url, so the org sync and the model catalogue sync stay off (an empty model mirror
refuses every model setting). Re-run with --org-sync-url https://<data api host> and --org-sync-key-file FILE."
elif [ ! -e "$secrets/admin/OrgSync__DataApiKey" ]; then
  gateway_note="
Data API: no key for $sync_url. Re-run with --org-sync-key-file FILE (a Data API key, catalog.read)."
fi
for svc in api admin worker; do [ -e "$secrets/$svc/Gateway__Keys__Eu" ] || missing="$missing $svc"; done
if [ -n "$missing" ]; then
  gateway_note="$gateway_note
Gateway: no Eu key for${missing}. P1a calls every model at ceiling Eu: re-run with --gateway-key-file Eu=FILE (or
SERVICE:Eu=FILE, one per deployable)."
fi
owui_note=""
if [ -n "$owui_host" ]; then
  owui_todo=""
  grep -q "^OPENAI_API_KEYS=." "$secrets/openwebui.env" || owui_todo="$owui_todo
  - the ume-rag-sk- key of its client in rag-api (created in the admin API): --openwebui-api-key-file FILE"
  # With --renew-certs deploy/.env is kept (and was read above).
  [ -n "${owui_rag_client:-${UME_RAG_OPENWEBUI_CLIENT_ID:-}}" ] || owui_todo="$owui_todo
  - that client's id, for rag-api's OpenWebUi:ClientId: --openwebui-rag-client-id ID"
  grep -q "^OAUTH_CLIENT_SECRET=." "$secrets/openwebui.env" || owui_todo="$owui_todo
  - the secret of its OIDC client: --openwebui-oidc-client-secret-file FILE"
  [ -z "$owui_todo" ] || owui_todo="
Still missing (re-run with these options; the key and the secret may also be filled in $secrets/openwebui.env):$owui_todo"
  owui_proxy=""
  [ "$proxy" = 1 ] || owui_proxy="
Without --proxy, Open WebUI can't reach rag-api: its connection uses rag-api's public URL https://$api_host$public_api,
which this host serves on $bind only, and https://$owui_host needs the reverse proxy too. Use --proxy for a working
Open WebUI; without it, these files only render."
  owui_note="$owui_proxy
Open WebUI (optional): register the redirect URI https://$owui_host/oauth/oidc/login/callback (identity design
§5.3) on its OIDC client '$owui_client'.$owui_todo
Then: docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait
and, as root, $here/openwebui-feedback-key.sh (the feedback service account's API key for rag-api)."
fi

cat <<EOF

Done. Generated:
  $env_file
  $certs   (internal CA, service certificates, Data Protection certificate)
  $secrets (database passwords, keys, connection strings, pgBackRest repository settings)

Next steps:
$own_step
  1. Register these redirect URIs on the OIDC client '$oidc_client' (code + PKCE; P1: the shared Keycloak's realm
     ume, from RAG's realm additions in deploy/keycloak/; P2: the Entra registration):
       https://$admin_host$public_admin/signin-oidc
       https://$admin_host$public_admin/signout-callback-oidc
  2. As root, before the first up: $here/host/install.sh (the ingestion slice the containers' cgroup_parent
     names, the firewall, and the backup, upload copy and restart-unhealthy timers; deploy/host/README.md).
  3. docker compose --env-file deploy/.env up -d --wait
  4. docker compose --env-file deploy/.env exec -T postgres pgbackrest --stanza=ragdb stanza-create
     Once per new database or repository (safe to repeat): WAL archiving fails until the stanza exists.$backup_note
  5. Escrow the long-lived keys: $here/escrow-keys.sh /media/<escrow>, then --verify it
     ($secrets/keys/rag-blob-internal and $secrets/pgbackrest/cipher-pass). Back up $secrets/.pepper and
     $certs/data-protection.pfx separately from database backups: without them a restored database is unusable.
  6. Internal certificates last 825 days; re-issue with: $0 --renew-certs
  7. Move $certs/ca.key off this host (with the backups in step 5). Anyone who can read it can
     issue certificates the services trust; it is only needed again for --renew-certs.
$proxy_note$rolling_note$gateway_note$owui_note
EOF
