#!/usr/bin/env bash
# First-run setup for the production Compose files (plans/platform/repo-and-deployment.md §6, §7.1): generates an
# internal CA, service certificates, the Data Protection certificate, the database passwords, the key pepper and the
# api's keys, the blob master key, the pgBackRest repository settings and deploy/.env. Safe to re-run: existing
# secrets are never overwritten (a new pepper, key, passphrase or Data Protection certificate would break existing
# data or backups); connection strings and other derived files are written again.
#
#   ./deploy/init-deployment.sh                      # prompts for what it needs
#   ./deploy/init-deployment.sh --oidc-authority https://login.microsoftonline.com/<tenant>/v2.0 \
#       --api-host rag-api.intern.umea.se --admin-host rag-admin.intern.umea.se \
#       --gateway-url https://llm.intern.umea.se --gateway-key-file OnPrem=onprem.key --gateway-key-file Eu=eu.key \
#       --backup-target /mnt/backup/ume-rag --allow-file-keys --yes
#   ./deploy/init-deployment.sh --renew-certs        # re-issue internal certificates only
#   ./deploy/init-deployment.sh --renew-ca           # new internal CA, then re-issue its certificates
#
# Options (defaults in brackets):
#   --oidc-authority URL, --oidc-client-id ID [ume-rag-admin], --oidc-client-secret-file FILE
#                           the admin BFF's own Entra registration (never the gateway's)
#   --api-host HOST, --admin-host HOST, --api-port N [8543], --admin-port N [9543], --bind ADDR [127.0.0.1]
#   --gateway-url URL       the LLM gateway (HTTPS); --ca-file FILE adds its CA (and your PKI chain) to the services'
#                           trust store
#   --gateway-key-file LEVEL=FILE
#                           a gateway key per level (OnPrem, Eu, External; repeat the option): api and admin get all,
#                           the worker only OnPrem and Eu
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
#   --openwebui-host HOST, --openwebui-oidc-client-id ID, --openwebui-oidc-provider-url URL,
#   --openwebui-data-dir DIR [deploy/openwebui-data]
#                           the optional Open WebUI stack (compose.openwebui.yaml): its secrets, settings and store
#   --role all [all]        multi-host roles (data, ingestion, ...) come with P2
#   --proxy                 a reverse proxy terminates public TLS and re-encrypts to this host; needs distinct
#                           hostnames and binds the listeners to 0.0.0.0 (firewall them to the proxy)
#   --upstream-host HOST    address the proxy uses to reach this host [hostname]
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
api_port=8543; admin_port=9543; bind=""; gateway_url=""; ca_file=""; org_sync_file=""
key_provider="file"; allow_file_keys=0; backup_target=""; s3_credentials=""; s3_region="us-east-1"
crawl_allow=""; host_memory=16; freshclam_proxy=""; owui_host=""; owui_client=""; owui_provider=""; owui_data=""; role="all"
proxy=0; upstream_host=""; api_cert=""; api_key=""; adm_cert=""; adm_key=""
yes=0; renew=0; renew_ca=0; chown=1
gateway_keys=()

usage() { sed -n '2,47p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
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
    --openwebui-oidc-provider-url) owui_provider="$2"; shift 2;;
    --openwebui-data-dir) owui_data="$2"; shift 2;;
    --role) role="$2"; shift 2;;
    --proxy) proxy=1; shift;;
    --upstream-host) upstream_host="$2"; shift 2;;
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
for f in "$oidc_secret_file" "$org_sync_file" "$ca_file" "$s3_credentials"; do
  [ -z "$f" ] || [ -r "$f" ] || die "cannot read $f"
done
for spec in ${gateway_keys[@]+"${gateway_keys[@]}"}; do
  case "${spec%%=*}" in OnPrem|Eu|External) ;; *) die "--gateway-key-file LEVEL=FILE: LEVEL is OnPrem, Eu or External";; esac
  [ -r "${spec#*=}" ] || die "cannot read ${spec#*=}"
done
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

for spec in ${gateway_keys[@]+"${gateway_keys[@]}"}; do
  level="${spec%%=*}"
  for svc in api admin worker; do
    # The worker never holds the External key (simplification pass §6).
    [ "$svc" = worker ] && [ "$level" = External ] && continue
    tr -d '\r\n' < "${spec#*=}" > "$secrets/$svc/Gateway__Keys__$level"
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
if [ -n "$owui_host" ]; then
  mkdir -p "$secrets/openwebui"
  owui_data="${owui_data:-$here/openwebui-data}"; mkdir -p "$owui_data"
  new_file "$secrets/openwebui/webui_secret_key" && rand > "$secrets/openwebui/webui_secret_key"
  # Read by `docker compose` (Open WebUI takes these only from its environment), so it stays owned by you. The two
  # empty values are yours to fill in; this file is never overwritten.
  if new_file "$secrets/openwebui.env"; then
    cat > "$secrets/openwebui.env" <<EOF
FORWARD_USER_INFO_HEADER_JWT_SECRET=$(cat "$secrets/.openwebui-jwt-secret")
# The ume-rag-sk- connection key of the Open WebUI client (issued in the admin UI).
OPENAI_API_KEYS=
# Open WebUI's own Entra registration.
OAUTH_CLIENT_SECRET=
EOF
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
UME_RAG_GATEWAY_URL=$gateway_url
UME_RAG_API_PUBLIC_URL=https://$api_host$public_api
UME_RAG_ADMIN_PUBLIC_HOST=$admin_host
# Listeners default to loopback (0.0.0.0 with --proxy: firewall them to the proxy only).
UME_RAG_API_BIND=$bind
UME_RAG_API_PORT=$api_port
UME_RAG_ADMIN_BIND=$bind
UME_RAG_ADMIN_PORT=$admin_port
# pgBackRest posix repository on the host (empty: the pgbackrest-repo volume, or unused with S3), the S3 endpoint
# (host firewall), and the upload copy's directory (deploy/backup-uploads.sh).
UME_RAG_BACKUP_DIR=$backup_dir
UME_RAG_BACKUP_ENDPOINT=$backup_endpoint
UME_RAG_UPLOAD_BACKUP_DIR=$upload_dir
UME_RAG_CRAWL_ALLOW=$crawl_allow
# freshclam's HTTP proxy (host firewall: the mirror network reaches only this).
UME_RAG_FRESHCLAM_PROXY=$freshclam_proxy
# UME_RAG_OTEL_ENDPOINT=
# Empty where deploy/host/rag-ingestion.slice is not installed (a developer machine):
# UME_RAG_INGESTION_SLICE=
# Pin images by digest once pushed to your registry (the release bundle does this), for example:
# API_IMAGE=ghcr.io/<owner>/ume-rag/api@sha256:...
EOF
  if [ -n "$owui_host" ]; then
    cat >> "$env_file" <<EOF
# Optional Open WebUI stack (compose.openwebui.yaml, a project of its own).
UME_RAG_OPENWEBUI_HOST=$owui_host
UME_RAG_OPENWEBUI_BIND=$bind
UME_RAG_OPENWEBUI_PORT=3000
UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=$owui_client
UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL=$owui_provider
UME_RAG_OPENWEBUI_DATA_DIR=$owui_data
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
  proxy_note="
Reverse proxy: forward https://$api_host to https://$upstream_host:$api_port and https://$admin_host to
https://$upstream_host:$admin_port, preserving the Host header; client_max_body_size 60m and proxy_request_buffering off
on PUT /api/upload-batches/{batchId}/files/{n} of the admin host (admin design §2.2), the gateway's 1 MB elsewhere.
Restrict those ports to the proxy with the host firewall."
fi
owui_note=""
if [ -n "$owui_host" ]; then
  owui_note="
Open WebUI (optional): fill in OPENAI_API_KEYS and OAUTH_CLIENT_SECRET in $secrets/openwebui.env and the
UME_RAG_OPENWEBUI_OIDC_* values in $env_file, then
  docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait"
fi

cat <<EOF

Done. Generated:
  $env_file
  $certs   (internal CA, service certificates, Data Protection certificate)
  $secrets (database passwords, keys, connection strings, pgBackRest repository settings)

Next steps:
$own_step
  1. Register these redirect URIs for client '$oidc_client' in Entra (code + PKCE):
       https://$admin_host$public_admin/signin-oidc
       https://$admin_host$public_admin/signout-callback-oidc
  2. docker compose --env-file deploy/.env up -d --wait
  3. docker compose --env-file deploy/.env exec -T postgres pgbackrest --stanza=ragdb stanza-create
     Once per new database or repository (safe to repeat): WAL archiving fails until the stanza exists.$backup_note
  4. Escrow the long-lived keys: $here/escrow-keys.sh /media/<escrow>, then --verify it
     ($secrets/keys/rag-blob-internal and $secrets/pgbackrest/cipher-pass). Back up $secrets/.pepper and
     $certs/data-protection.pfx separately from database backups: without them a restored database is unusable.
  5. As root: $here/host/install.sh (the ingestion slice, the firewall, and the backup, upload copy and
     restart-unhealthy timers; deploy/host/README.md).
  6. Internal certificates last 825 days; re-issue with: $0 --renew-certs
  7. Move $certs/ca.key off this host (with the backups in step 4). Anyone who can read it can
     issue certificates the services trust; it is only needed again for --renew-certs.
$proxy_note$owui_note
EOF
