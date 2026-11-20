#!/usr/bin/env bash
# Generates the certificates and secrets of a Kubernetes installation and creates (or updates) its Kubernetes secrets
# (docs/kubernetes.md). The files come from deploy/portainer/bootstrap.sh, the same script that the Compose and
# Portainer stacks run as kalla-bootstrap (Decisions "One deployment bootstrap"), run here on your machine into
# deploy/certs and deploy/secrets. Safe to run again: existing secrets are never overwritten, service certificates are
# re-issued within 30 days of their expiry, and a value left out keeps what the directories hold.
#
#   ./deploy/kubernetes/create-secrets.sh --namespace kalla --domain example.se \
#       --gateway-credential-key-file api=kalla-api.key --gateway-credential-key-file admin=kalla-admin.key \
#       --gateway-credential-key-file worker=kalla-worker.key \
#       --datastores --backup-target s3://s3.example.se/kalla --backup-s3-credentials-file s3.cred [--renderer]
#
#   --namespace NS     namespace of the overlay (created if missing)
#   --domain DOMAIN    the base domain: kalla-api's and kalla-admin's host names default to rag.DOMAIN and rag-admin.DOMAIN
#   --api-host HOST, --admin-host HOST
#                      the public host names when they are not the domain's defaults (the admin's defaults to kalla-api's
#                      without --domain); they go into the two services' certificates
#   --gateway-credential-key-file SERVICE=FILE
#                      SERVICE's credential key of the gateway integration Källa (api, admin or worker; repeat the
#                      option), issued by a gateway admin: the secret file Gateway__Key
#   --ca-file FILE     a CA certificate the services trust besides the internal CA (the gateway's, the sign-in
#                      provider's, your PKI chain); repeat the option. Give it on every run
#   --oidc-client-secret-file FILE, --oidc-refuse-guests true|false
#                      optional: declares sign-in instead of the setup wizard (with Oidc__Authority in the overlay)
#   --api-cert FILE --api-key FILE --admin-cert FILE --admin-key FILE
#                      certificates from your PKI for the two listeners instead of the internal CA. Give them on every
#                      run
#   --postgres-host HOST
#                      your own database's host name instead of the component's Service `postgres` (give it on every
#                      run). Its roles' passwords are <secrets dir>/postgres/<role>_password (migrator, api, admin,
#                      worker): generated on the first run, so set the roles' passwords from them, or replace the files
#                      and run again. Its CA, when it is not the internal one: --ca-file
#   --datastores       also the secrets of components/datastores (in-cluster Postgres and its pgBackRest repository)
#   --backup-target s3://ENDPOINT/BUCKET[/PATH]|local
#                      the pgBackRest repository of --datastores: an S3-compatible store (with
#                      --backup-s3-credentials-file FILE: access key and secret key on two lines, and
#                      --backup-s3-region R [us-east-1]), or `local` (the component's volume: not a backup off the
#                      cluster). Needed on the first run with --datastores
#   --renderer         also the renderer's WebSocket path (components/renderer) and the worker's Render__Endpoint;
#                      without it the endpoint is left out, so the worker refuses rendering instead of waiting
#   --certs-dir DIR [deploy/certs], --secrets-dir DIR [deploy/secrets]
#   --generate-only    write the files and create no Kubernetes secret (needs no kubectl)
#
# Secrets: kalla-<service>-secrets (files the app reads as settings, e.g. ConnectionStrings__ragdb, the gateway
# credential key Gateway__Key of api, admin and worker, kalla-admin's setup secret Setup__Secret and, when sign-in is
# declared instead of set in the setup wizard, its client secret Oidc__ClientSecret) and
# kalla-<service>-certs (TLS certificate, the CAs it trusts, Data Protection certificate) for api, admin, worker and
# migrations, kalla-parser-certs, and kalla-blob-key (the blob master key). The connection strings point at the
# host "postgres" (--postgres-host for your own database) and are written again on every run from
# <secrets dir>/postgres/<role>_password.
set -euo pipefail
umask 077
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
certs="$here/../certs"; secrets="$here/../secrets"; ns=""; datastores=0; renderer=0; generate_only=0
domain=""; api_host=""; admin_host=""; oidc_secret_file=""; oidc_refuse_guests=""
backup_target=""; s3_credentials=""; s3_region=""
postgres_host="postgres"
api_cert=""; api_key=""; adm_cert=""; adm_key=""
credential_keys=(); ca_files=()
die() { echo "error: $*" >&2; exit 1; }
while [ $# -gt 0 ]; do
  case "$1" in
    --namespace|-n) ns="$2"; shift 2;;
    --domain) domain="$2"; shift 2;;
    --api-host) api_host="$2"; shift 2;;
    --admin-host) admin_host="$2"; shift 2;;
    --gateway-credential-key-file) credential_keys+=("$2"); shift 2;;
    --ca-file) ca_files+=("$2"); shift 2;;
    --oidc-client-secret-file) oidc_secret_file="$2"; shift 2;;
    --oidc-refuse-guests) oidc_refuse_guests="$2"; shift 2;;
    --api-cert) api_cert="$2"; shift 2;;
    --api-key) api_key="$2"; shift 2;;
    --admin-cert) adm_cert="$2"; shift 2;;
    --admin-key) adm_key="$2"; shift 2;;
    --postgres-host) postgres_host="$2"; shift 2;;
    --datastores) datastores=1; shift;;
    --backup-target) backup_target="$2"; shift 2;;
    --backup-s3-credentials-file) s3_credentials="$2"; shift 2;;
    --backup-s3-region) s3_region="$2"; shift 2;;
    --renderer) renderer=1; shift;;
    --certs-dir) certs="$2"; shift 2;;
    --secrets-dir) secrets="$2"; shift 2;;
    --generate-only) generate_only=1; shift;;
    -h|--help) awk 'NR > 1 && !/^#/ { exit } NR > 1 { sub(/^# ?/, ""); print }' "${BASH_SOURCE[0]}"; exit 0;;
    *) die "unknown option: $1";;
  esac
done
bootstrap="$here/../portainer/bootstrap.sh"
[ -r "$bootstrap" ] || die "$bootstrap is missing: it generates the certificates and secrets"
command -v openssl >/dev/null || die "openssl is required"
[ "$generate_only" = 1 ] || [ -n "$ns" ] || die "--namespace is required"
if [ -n "$domain" ]; then
  [[ "$domain" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] || die "--domain takes a domain name such as example.se: $domain"
  api_host="${api_host:-rag.$domain}"; admin_host="${admin_host:-rag-admin.$domain}"
fi
[ -n "$api_host" ] || die "--domain or --api-host is required (the host names go into the services' certificates)"
case "$oidc_refuse_guests" in ""|true|false) ;; *) die "--oidc-refuse-guests must be true or false";; esac
for f in "$oidc_secret_file" "$s3_credentials" ${ca_files[@]+"${ca_files[@]}"}; do
  [ -z "$f" ] || [ -r "$f" ] || die "cannot read $f"
done
if [ -n "$api_cert$api_key$adm_cert$adm_key" ]; then
  for f in "$api_cert" "$api_key" "$adm_cert" "$adm_key"; do
    if [ -z "$f" ] || [ ! -r "$f" ]; then die "your own certificates need readable --api-cert, --api-key, --admin-cert and --admin-key"; fi
  done
fi
case "$backup_target" in
  ""|local) ;;
  s3://*) [ -n "$s3_credentials" ] || die "an s3:// backup target needs --backup-s3-credentials-file";;
  *) die "--backup-target is s3://ENDPOINT/BUCKET[/PATH] or local";;
esac

# --- What the bootstrap reads besides its variables: the CAs to trust and your own certificates ---------------------
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
mkdir -p "$work/trust" "$work/pki" "$work/none" "$certs" "$secrets"
n=0
for f in ${ca_files[@]+"${ca_files[@]}"}; do n=$((n + 1)); cp "$f" "$work/trust/$n.pem"; done
if [ -n "$api_cert" ]; then
  cp "$api_cert" "$work/pki/api.crt"; cp "$api_key" "$work/pki/api.key"
  cp "$adm_cert" "$work/pki/admin.crt"; cp "$adm_key" "$work/pki/admin.key"
fi

# --- Values that follow a variable in the bootstrap: an option's file, else what the directories already hold --------
# (kalla-bootstrap removes such a file when its variable is empty, which is right for a stack and wrong for a re-run here.)
held() { [ ! -r "$1" ] || tr -d '\r\n' < "$1"; }
declare -A key=()
for svc in api admin worker; do key[$svc]="$(held "$secrets/$svc/Gateway__Key")"; done
for spec in ${credential_keys[@]+"${credential_keys[@]}"}; do
  [ "${spec#*=}" != "$spec" ] || die "--gateway-credential-key-file takes SERVICE=FILE, not $spec"
  case "${spec%%=*}" in
    api|admin|worker) ;;
    *) die "--gateway-credential-key-file SERVICE=FILE: SERVICE is api, admin or worker, not ${spec%%=*}";;
  esac
  [ -r "${spec#*=}" ] || die "cannot read ${spec#*=}"
  key[${spec%%=*}]="$(held "${spec#*=}")"
done
oidc_secret="$(held "$secrets/admin/Oidc__ClientSecret")"
[ -z "$oidc_secret_file" ] || oidc_secret="$(held "$oidc_secret_file")"
[ -n "$oidc_refuse_guests" ] || oidc_refuse_guests="$(held "$secrets/admin/Oidc__RefuseGuests")"
s3_key=""; s3_key_secret=""
if [ -n "$s3_credentials" ]; then
  s3_key="$(sed -n 1p "$s3_credentials" | tr -d '\r')"; s3_key_secret="$(sed -n 2p "$s3_credentials" | tr -d '\r')"
fi
profiles=""; [ "$renderer" = 0 ] || profiles="render"

# The bootstrap's own closing lines are about a stack (its variables, its guide), so they are shown only when it fails.
if ! env KALLA_BOOTSTRAP_CERTS="$certs" KALLA_BOOTSTRAP_SECRETS="$secrets" KALLA_BOOTSTRAP_CONF="" \
    KALLA_BOOTSTRAP_OWN=no KALLA_BOOTSTRAP_GATEWAY_CERTS="$work/none" KALLA_BOOTSTRAP_GATEWAY_CA=optional \
    KALLA_BOOTSTRAP_TRUST="$work/trust" KALLA_BOOTSTRAP_PKI="$work/pki" \
    KALLA_BOOTSTRAP_INTEGRATION_SECRETS="$work/none" KALLA_POSTGRES_HOST="$postgres_host" \
    FORS_DOMAIN="$domain" KALLA_API_HOST="$api_host" KALLA_ADMIN_HOST="$admin_host" COMPOSE_PROFILES="$profiles" \
    KALLA_API_GATEWAY_KEY="${key[api]}" KALLA_ADMIN_GATEWAY_KEY="${key[admin]}" \
    KALLA_WORKER_GATEWAY_KEY="${key[worker]}" KALLA_OIDC_CLIENT_SECRET="$oidc_secret" \
    KALLA_OIDC_REFUSE_GUESTS="$oidc_refuse_guests" KALLA_BACKUP_TARGET="$backup_target" \
    KALLA_BACKUP_S3_KEY="$s3_key" KALLA_BACKUP_S3_KEY_SECRET="$s3_key_secret" KALLA_BACKUP_S3_REGION="$s3_region" \
    sh "$bootstrap" > "$work/bootstrap.log" 2>&1; then
  cat "$work/bootstrap.log" >&2
  die "generating the certificates and secrets failed (above)"
fi
grep 'issuing certificate' "$work/bootstrap.log" | sed 's/^kalla-bootstrap: //' || true
echo "Certificates in $certs, secrets in $secrets"
if [ "$datastores" = 1 ] && [ ! -r "$secrets/pgbackrest/repo.conf" ]; then
  die "--datastores needs a pgBackRest repository: pass --backup-target s3://ENDPOINT/BUCKET[/PATH] or local"
fi

if [ "$generate_only" = 0 ]; then
  kubectl get namespace "$ns" >/dev/null 2>&1 || kubectl create namespace "$ns"
  apply() { kubectl -n "$ns" create secret generic "$@" --dry-run=client -o yaml | kubectl apply -f - >/dev/null; echo "secret/$1"; }
  secret() { # secret NAME DIR [SKIP]: every regular file in DIR becomes a key, except SKIP (and the bootstrap's dotfiles)
    local args=() f
    for f in "$2"/*; do
      [ -f "$f" ] || continue
      [ "$(basename "$f")" != "${3:-}" ] || continue
      args+=("--from-file=$f")
    done
    apply "$1" "${args[@]}"
  }
  for svc in api admin migrations; do
    secret "kalla-$svc-secrets" "$secrets/$svc"
    secret "kalla-$svc-certs" "$certs/$svc"
  done
  # ClamAv__Mode is the stack's (it follows a Compose profile); here the mode is the overlay's kalla-worker-config.
  secret kalla-worker-secrets "$secrets/worker" ClamAv__Mode
  if [ "$renderer" = 1 ]; then
    printf 'KALLA_RENDER_WS_PATH=%s\n' "$(tr -d '\r\n' < "$secrets/renderer/ws-path")" > "$work/renderer.env"
    apply kalla-renderer-env --from-env-file="$work/renderer.env"
  fi
  secret kalla-worker-certs "$certs/worker"
  secret kalla-parser-certs "$certs/parser"
  apply kalla-blob-key --from-file="$secrets/keys/rag-blob-internal"
  if [ "$datastores" = 1 ]; then
    secret kalla-postgres-secrets "$secrets/postgres"
    secret kalla-postgres-certs "$certs/postgres"
    apply kalla-pgbackrest --from-file="$secrets/pgbackrest/repo.conf"
  fi
fi

missing=""
for svc in api admin worker; do [ -s "$secrets/$svc/Gateway__Key" ] || missing="$missing $svc"; done
[ -z "$missing" ] || echo "Gateway: no credential key for${missing}; a service without one has its model calls refused. Run \
again with --gateway-credential-key-file SERVICE=FILE (docs/production-setup.md, Gateway credential keys)."
echo "Escrow the keys outside the cluster: without them a restored database is unusable (runbook, Key escrow):"
echo "  deploy/escrow-keys.sh --secrets-dir $secrets --certs-dir $certs /media/<escrow>"
echo "Keep $certs/ca.key offline between runs that issue a certificate."
if [ ! -s "$secrets/admin/Oidc__ClientSecret" ]; then
  echo "Sign-in: no client secret here, so after deploy.sh set it up in the setup wizard: the setup code is in"
  echo "kubectl -n ${ns:-<namespace>} logs deploy/kalla-admin (either pod), the wizard at https://${admin_host:-$api_host}/setup"
  echo "(docs/kubernetes.md)."
fi
