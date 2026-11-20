#!/usr/bin/env bash
# Writes deploy/.env for Docker Compose: the variables of deploy/compose.rag.yaml (the Portainer stack's names) and
# COMPOSE_FILE with the overlays in use. It generates no certificate or secret: rag-bootstrap does, in the volumes, at
# the first `up`, and ends its log with what is still missing and the setup wizard's address. Sign-in is set in the
# setup wizard (https://<admin host>:9543/setup without a proxy, the setup code in rag-admin's log); only with
# --hardened is the provider's address asked for, since the host firewall lets rag-admin reach that provider alone. A
# re-run changes what its options name and keeps every other line. A secret it is given (a key file, S3 credentials)
# is kept in deploy/.env, mode 600, as Portainer keeps it in the stack's variables.
#
#   deploy/init-deployment.sh                       # asks for the domain and the gateway's address
#   deploy/init-deployment.sh --domain example.se --gateway-url https://llm.example.se --backup-target /mnt/backup --yes
#   docker compose --env-file deploy/.env up -d --wait
#
# Options (each sets the variable it names):
#   --domain DOMAIN          UME_DOMAIN: rag-api, rag-admin and Open WebUI are rag., rag-admin. and chat.DOMAIN
#   --api-host HOST, --admin-host HOST
#                            UME_RAG_API_HOST, UME_RAG_ADMIN_HOST: a name of its own instead
#   --gateway-url URL        UME_RAG_GATEWAY_URL: the gateway's https URL (required)
#   --gateway-ca-file FILE   the gateway's CA certificate (a gateway on this host: deploy/certs/ca.pem of its
#                            directory), copied to deploy/gateway-ca/ca.pem, which UME_RAG_GATEWAY_CA_DIR then names;
#                            give it again when the gateway gets a new CA. Not needed for a gateway with a public
#                            certificate. (Only the certificate is copied: the gateway's directory also holds its keys.)
#   --gateway-secrets-dir DIR  UME_RAG_GATEWAY_SECRETS_DIR: the credential keys a gateway on this host wrote from
#                            Ume.Rag's manifest (deploy/portainer/integration-manifest.json): DIR/Ume.Rag/rag-api,
#                            rag-admin and rag-worker
#   --gateway-credential-key-file SERVICE=FILE
#                            UME_RAG_<SERVICE>_GATEWAY_KEY: a key issued by hand for api, admin or worker; wins
#   --profiles LIST          COMPOSE_PROFILES [clamav]: clamav, render, openwebui and proxy, comma-separated
#   --backup-target TARGET   UME_RAG_BACKUP_TARGET, with compose.backup.yaml: a directory on the backup share
#                            (DIR/pgbackrest and DIR/uploads), s3://ENDPOINT/BUCKET[/PATH], or local (two volumes: test
#                            hosts only)
#   --backup-s3-credentials-file FILE  the access key and the secret key on two lines; --backup-s3-region REGION
#   --host-memory 16|24      UME_RAG_HOST_MEMORY; 24 adds compose.host-24g.yaml and compose.clamav-conf.yaml
#   --freshclam-proxy HOST:PORT  UME_RAG_FRESHCLAM_PROXY, with compose.clamav-conf.yaml
#   --hardened               compose.hardened.yaml: the host firewall, the ingestion slice (deploy/host/install.sh)
#   --oidc-authority URL     UME_RAG_OIDC_AUTHORITY: the sign-in provider (asked for with --hardened)
#   --proxy-address LIST     UME_RAG_PROXY_SOURCES: your reverse proxy's IPv4 addresses or CIDRs, which the host firewall
#                            alone admits to the published ports
#   --rolling                compose.rolling.yaml: a second rag-api and rag-admin (deploy/rolling-update.sh)
#   --overlays LIST          the overlays exactly: backup, hardened, host-24g, clamav-conf, rolling (empty: none)
#   --set NAME=VALUE         any other variable (deploy/portainer/stack.env.example lists them); empty removes it,
#                            except UME_RAG_INGESTION_SLICE, whose empty value is kept (a host without the slice)
#   --set-file NAME=FILE     the same with FILE's first line as the value, for a secret (UME_RAG_OIDC_CLIENT_SECRET)
#   --yes                    no questions: a missing required value stops the run
set -euo pipefail
umask 077
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/.env"; yes=0; overlays_given=""; add=(); sets=()
usage() { awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }
readable() { [ -r "$1" ] && [ -f "$1" ] || die "cannot read $1"; }
# dir_of DIR: a directory this user can enter (#874: a gateway's integration-secrets is the services' uid's, mode 700).
dir_of() {
  [ -d "$1" ] || die "no directory $1"
  [ -x "$1" ] || die "cannot enter $1 as $(id -un): run deploy/init-deployment.sh as root (sudo) or as the directory's owner"
}

declare -A val=(); order=()
put() { [ -n "${val[$1]+x}" ] || order+=("$1"); val[$1]="$2"; }
get() { echo "${val[$1]:-}"; }
# The current deploy/.env, read (never sourced: it is Compose's file, not a shell script), quotes removed.
if [ -f "$env_file" ]; then
  while IFS= read -r line; do
    [[ "$line" =~ ^([A-Z_][A-Z0-9_]*)=(.*)$ ]] || continue
    n="${BASH_REMATCH[1]}"; v="${BASH_REMATCH[2]}"
    if [[ "$v" =~ ^\'(.*)\'$ || "$v" =~ ^\"(.*)\"$ ]]; then v="${BASH_REMATCH[1]}"; fi
    put "$n" "$v"
  done < "$env_file"
fi
# The overlays deploy/.env uses already.
overlays=" $(get COMPOSE_FILE | tr ':' '\n' | sed -n 's#.*/compose\.\(.*\)\.yaml$#\1#p' | sed '/^rag$/d' | tr '\n' ' ')"

while [ $# -gt 0 ]; do
  case "$1" in
    --hardened|--rolling) add+=("${1#--}"); shift; continue;;
    --yes|-y) yes=1; shift; continue;;
    -h|--help) usage 0;;
    -*) [ $# -ge 2 ] || die "$1 needs a value";;
  esac
  case "$1" in
    --domain) sets+=("UME_DOMAIN=$2");; --api-host) sets+=("UME_RAG_API_HOST=$2");;
    --admin-host) sets+=("UME_RAG_ADMIN_HOST=$2");; --gateway-url) sets+=("UME_RAG_GATEWAY_URL=${2%/}");;
    --gateway-ca-file)
      readable "$2"; grep -q 'BEGIN CERTIFICATE' "$2" || die "$2 is not a PEM certificate"
      ! grep -q 'PRIVATE KEY' "$2" || die "$2 holds a private key: give the CA's certificate only"
      mkdir -p "$here/gateway-ca"; (umask 022; cat "$2" > "$here/gateway-ca/ca.pem"); chmod 755 "$here/gateway-ca"
      sets+=("UME_RAG_GATEWAY_CA_DIR=$here/gateway-ca");;
    --gateway-secrets-dir) dir_of "$2"; sets+=("UME_RAG_GATEWAY_SECRETS_DIR=$(cd "$2" && pwd)");;
    --gateway-credential-key-file)
      svc="${2%%=*}"; case "$svc" in api|admin|worker) ;; *) die "$1 SERVICE=FILE: SERVICE is api, admin or worker";; esac
      readable "${2#*=}"; sets+=("UME_RAG_${svc^^}_GATEWAY_KEY=$(sed -n 1p "${2#*=}" | tr -d '\r')");;
    --profiles) sets+=("COMPOSE_PROFILES=$2");;
    --backup-target) sets+=("UME_RAG_BACKUP_TARGET=${2%/}"); add+=(backup);;
    --backup-s3-credentials-file)
      readable "$2"
      sets+=("UME_RAG_BACKUP_S3_KEY=$(sed -n 1p "$2" | tr -d '\r')" "UME_RAG_BACKUP_S3_KEY_SECRET=$(sed -n 2p "$2" | tr -d '\r')");;
    --backup-s3-region) sets+=("UME_RAG_BACKUP_S3_REGION=$2");;
    --host-memory) sets+=("UME_RAG_HOST_MEMORY=$2");;
    --freshclam-proxy) sets+=("UME_RAG_FRESHCLAM_PROXY=$2"); add+=(clamav-conf);;
    --oidc-authority) sets+=("UME_RAG_OIDC_AUTHORITY=$2");; --proxy-address) sets+=("UME_RAG_PROXY_SOURCES=$2");;
    --overlays) overlays_given=1; overlays=" ${2//,/ } ";;
    --set) sets+=("$2");;
    --set-file) readable "${2#*=}"; sets+=("${2%%=*}=$(sed -n 1p "${2#*=}" | tr -d '\r')");;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
  shift 2
done

# Retired variables (Decisions "Deployment values across releases"): the names in compose.rag.yaml's UME_RAG_RETIRED_SET.
retired=" $(grep -oE '\$\{[A-Z0-9_]+:\+[A-Z0-9_]+\}' "$here/compose.rag.yaml" | sed -nE 's/^\$\{([A-Z0-9_]+):\+\1\}$/\1/p' | tr '\n' ' ') "
for s in ${sets[@]+"${sets[@]}"}; do
  name="${s%%=*}"; v="${s#*=}"
  [[ "$s" == *=* && "$name" =~ ^[A-Z][A-Z0-9_]*$ ]] || die "--set takes NAME=VALUE, not $s"
  [ "$name" != COMPOSE_FILE ] || die "COMPOSE_FILE follows the overlays: use --overlays"
  [[ "$retired" != *" $name "* ]] || die "$name is no longer read (docs/upgrade-guide.md says what replaced it)"
  case "$v" in *"'"*|*$'\n'*) die "the value of $name contains ' or a line break";; esac
  put "$name" "$v"
done
# A first run's profiles: the malware scan. Set and empty (--profiles ''), none.
[ -n "${val[COMPOSE_PROFILES]+x}" ] || put COMPOSE_PROFILES clamav

ask() { # ask NAME PROMPT: NAME from the terminal unless it has a value; required with --yes
  [ -z "$(get "$1")" ] || return 0
  [ "$yes" = 0 ] && [ -t 0 ] || die "$2 is required (see --help)"
  local reply; read -r -p "$2: " reply; put "$1" "${reply%/}"
}
if [ -z "$(get UME_DOMAIN)" ] && [ -z "$(get UME_RAG_API_HOST)" ]; then ask UME_DOMAIN "The domain (rag.<domain> is rag-api's name)"; fi
ask UME_RAG_GATEWAY_URL "The gateway's URL (https://...)"

for o in ${add[@]+"${add[@]}"}; do [[ "$overlays" == *" $o "* ]] || overlays="$overlays $o "; done
case "$(get UME_RAG_HOST_MEMORY)" in
  ""|16) overlays="${overlays// host-24g / }"; [ -n "$(get UME_RAG_FRESHCLAM_PROXY)" ] || [ -n "$overlays_given" ] || overlays="${overlays// clamav-conf / }";;
  24) for o in host-24g clamav-conf; do [[ "$overlays" == *" $o "* ]] || overlays="$overlays $o "; done;;
  *) die "--host-memory is 16 or 24";;
esac
if [[ "$overlays" == *" hardened "* ]] && [ -z "$(get UME_RAG_OIDC_AUTHORITY)" ] && [ "$yes" = 0 ] && [ -t 0 ]; then
  echo "With the host firewall rag-admin reaches only the sign-in provider named here, and the setup wizard's test sign-in"
  echo "needs it. Keycloak: https://<host>/realms/<realm>; Entra ID: https://login.microsoftonline.com/<tenant>/v2.0."
  read -r -p "The sign-in provider's address (empty: none for now): " reply; put UME_RAG_OIDC_AUTHORITY "$reply"
fi

# Checks, then what follows from the values.
[ -n "$(get UME_DOMAIN)$(get UME_RAG_API_HOST)" ] || die "--domain or --api-host is required"
for n in UME_DOMAIN UME_RAG_API_HOST UME_RAG_ADMIN_HOST UME_RAG_OPENWEBUI_HOST; do
  v="$(get "$n")"; [ -z "$v" ] || [[ "$v" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] || die "$n takes a host or domain name, not $v"
done
case "$(get UME_RAG_GATEWAY_URL)" in https://*) ;; *) die "the gateway's URL is an https:// URL";; esac
case "$(get UME_RAG_OIDC_AUTHORITY)" in ""|https://*) ;; *) die "the sign-in provider's address is an https:// URL";; esac
for p in $(get COMPOSE_PROFILES | tr ',' ' '); do
  case "$p" in clamav|render|openwebui|proxy) ;; *) die "unknown profile $p: clamav, render, openwebui or proxy";; esac
done
for o in $overlays; do
  case "$o" in backup|hardened|host-24g|clamav-conf|rolling) ;; *) die "unknown overlay $o";; esac
done
for a in $(get UME_RAG_PROXY_SOURCES | tr ',' ' '); do
  [[ "$a" =~ ^[0-9]+(\.[0-9]+){3}(/[0-9]+)?$ ]] || die "--proxy-address takes IPv4 addresses or CIDRs, comma-separated: $a"
done
[ -z "$(get UME_RAG_FRESHCLAM_PROXY)" ] || [[ "$(get UME_RAG_FRESHCLAM_PROXY)" =~ ^[A-Za-z0-9.-]+:[0-9]+$ ]] || die "--freshclam-proxy is HOST:PORT"
target="$(get UME_RAG_BACKUP_TARGET)"
case "$target" in
  "") [[ "$overlays" != *" backup "* ]] || die "the backup overlay needs --backup-target";;
  s3://*/?*) [ -n "$(get UME_RAG_BACKUP_S3_KEY)" ] && [ -n "$(get UME_RAG_BACKUP_S3_KEY_SECRET)" ] \
      || die "an s3:// backup target needs --backup-s3-credentials-file"
    t="${target#s3://}"; put UME_RAG_BACKUP_ENDPOINT "${t%%/*}"; put UME_RAG_BACKUP_DIR ""; put UME_RAG_UPLOAD_BACKUP_DIR "";;
  local) put UME_RAG_BACKUP_ENDPOINT ""; put UME_RAG_BACKUP_DIR ""; put UME_RAG_UPLOAD_BACKUP_DIR "";;
  /*) put UME_RAG_BACKUP_ENDPOINT ""; put UME_RAG_BACKUP_DIR "$target/pgbackrest"; put UME_RAG_UPLOAD_BACKUP_DIR "$target/uploads";;
  *) die "--backup-target is an absolute path, s3://ENDPOINT/BUCKET[/PATH] or local";;
esac
compose_file="$here/compose.rag.yaml"
for o in backup hardened host-24g clamav-conf rolling; do
  [[ "$overlays" != *" $o "* ]] || compose_file="$compose_file:$here/compose.$o.yaml"
done
[[ "$compose_file" != *"'"* ]] || die "the path $here contains '"

tmp="$(mktemp "$env_file.XXXXXX")"
{
  echo "# Written by deploy/init-deployment.sh (its --help lists the options); edit it with a re-run, or by hand."
  printf "COMPOSE_FILE='%s'\n" "$compose_file"
  for n in "${order[@]}"; do
    # An empty value is left out, but an empty COMPOSE_PROFILES stays (none, not a first run's default), and an empty
    # UME_RAG_INGESTION_SLICE (no slice on this host, where unset means rag-ingestion.slice).
    if [ "$n" != COMPOSE_FILE ] && { [ -n "${val[$n]}" ] || [ "$n" = COMPOSE_PROFILES ] || [ "$n" = UME_RAG_INGESTION_SLICE ]; }; then printf "%s='%s'\n" "$n" "${val[$n]}"; fi
  done
} > "$tmp"
chmod 600 "$tmp"; mv -f "$tmp" "$env_file"

admin="$(get UME_RAG_ADMIN_HOST)"; [ -n "$admin" ] || admin="${val[UME_DOMAIN]:+rag-admin.$(get UME_DOMAIN)}"
api="$(get UME_RAG_API_HOST)"; [ -n "$api" ] || api="${val[UME_DOMAIN]:+rag.$(get UME_DOMAIN)}"
# The setup wizard's address, as rag-bootstrap gives it (#874): through the TLS proxy (profile proxy) when rag-admin has
# a host name of its own, else on rag-admin's port.
if [[ ",$(get COMPOSE_PROFILES)," == *,proxy,* ]] && [ -n "$admin" ] && [ "$admin" != "$api" ]; then
  port="$(get UME_RAG_PROXY_PORT)"; port="${port:-443}"
else
  port="$(get UME_RAG_ADMIN_PORT)"; port="${port:-9543}"
fi
admin="${admin:-$api}"; [ "$port" = 443 ] || admin="$admin:$port"
dc="docker compose --env-file $env_file"
echo "Wrote $env_file (mode 600). Next:"
[[ "$overlays" != *" hardened "* ]] || echo "  sudo $here/host/install.sh    # before the first up: the slice, the firewall, the timers"
echo "  $dc up -d --wait"
echo "  $dc logs rag-bootstrap    # its last lines: what is still missing, and the setup wizard's address"
echo "  $dc logs rag-admin | grep 'Setup code'    # for https://$admin/setup"
if [[ "$overlays" == *" backup "* ]]; then
  echo "  $dc exec -T rag-postgres pgbackrest --stanza=ragdb stanza-create    # once per database or repository"
  [[ "$overlays" == *" hardened "* ]] || echo "  sudo $here/host/install.sh    # the backup and certificate timers"
fi
echo "  $here/escrow-keys.sh /media/<escrow>    # every key a restored database needs: without them it is unusable"
[[ ",$(get COMPOSE_PROFILES)," == *,proxy,* ]] || [ -n "$(get UME_RAG_PROXY_SOURCES)" ] || [ -n "$(get UME_RAG_API_PUBLIC_URL)" ] \
  || echo "Without a TLS proxy (the profile proxy, or yours) rag-api is https://<its name>:8543: --set UME_RAG_API_PUBLIC_URL=..."
if [[ "$overlays" == *" hardened "* ]] && [ -z "$(get UME_RAG_OIDC_AUTHORITY)" ]; then
  echo "The host firewall lets rag-admin reach no sign-in provider yet: run again with --oidc-authority URL."
fi
