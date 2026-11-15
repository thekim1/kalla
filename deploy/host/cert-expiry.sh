#!/usr/bin/env bash
# Warns before an internal certificate expires (#705): the internal CA (ca.pem, 10 years) and each service certificate
# (DIR/DIR.crt, 825 days) under the deployment's certificate directory. Nothing renews them on its own in a Compose
# deployment, and Postgres's expired certificate fails every VerifyFull connection. rag-cert-expiry.timer runs this
# daily; the service fails (systemctl --failed, the journal) while a certificate expires within the warning window.
#
#   deploy/host/cert-expiry.sh [--days N]
#
# Options:
#   --days N            [60] warn when a certificate expires within N days
#   --env-file FILE     [deploy/.env] the deployment: its UME_RAG_CERTS_DIR
#   --certs-dir DIR     [UME_RAG_CERTS_DIR, else deploy/certs] the certificate directory (a Portainer host: the
#                       rag-certs volume's directory, for example /var/lib/docker/volumes/<stack>_rag-certs/_data)
#   --state-dir DIR     [$STATE_DIRECTORY, else /var/lib/ume-rag] where certs.prom goes
#
# Metrics (Prometheus text format in DIR/certs.prom, written atomically, next to backup.prom):
#   rag_cert_expiry_timestamp_seconds{file}   the certificate's notAfter
#   rag_cert_check_timestamp_seconds          when they were read
# Alert (proposed): rag_cert_expiry_timestamp_seconds - time() < 60 * 86400, or rag-cert-expiry.service failed.
set -euo pipefail
umask 027

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy_dir="$(dirname "$here")"
env_file="$deploy_dir/.env"; certs=""; days=60; state_dir="${STATE_DIRECTORY:-/var/lib/ume-rag}"

usage() { sed -n '2,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --days) days="${2:-}"; shift 2;;
    --env-file) env_file="${2:-}"; shift 2;;
    --certs-dir) certs="${2:-}"; shift 2;;
    --state-dir) state_dir="${2:-}"; shift 2;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done
case "$days" in ''|*[!0-9]*) die "--days must be a whole number";; esac
[ -n "$certs" ] || certs="$(sed -n 's/^UME_RAG_CERTS_DIR=//p' "$env_file" 2>/dev/null | tail -n 1 || true)"
certs="${certs:-$deploy_dir/certs}"
[ -r "$certs/ca.pem" ] || die "cannot read $certs/ca.pem: run as root, or pass --certs-dir"

# rag-bootstrap of the Portainer stack writes .san next to each certificate it issues, init-deployment.sh does not.
portainer=0; [ -e "$certs/postgres/.san" ] && portainer=1
now="$(date +%s)"; warn=0; metrics=""
for crt in "$certs/ca.pem" "$certs"/*/*.crt; do
  [ -e "$crt" ] || continue
  file="${crt#"$certs"/}"
  if ! end="$(openssl x509 -enddate -noout -in "$crt" 2>/dev/null)"; then
    echo "warning: $file is not a readable certificate"; warn=1; continue
  fi
  end="$(date -u -d "${end#notAfter=}" +%s)"
  left=$(( (end - now) / 86400 ))
  metrics="$metrics
rag_cert_expiry_timestamp_seconds{file=\"$file\"} $end"
  if [ "$left" -ge "$days" ]; then
    echo "ok: $file expires $(date -u -d "@$end" +%F) ($left days)"
    continue
  fi
  warn=1
  svc="$(basename "$(dirname "$crt")")"
  if [ "$portainer" = 1 ] && [ "$file" = ca.pem ]; then
    how="replace the CA (docs/portainer.md, Internal certificates)"
  elif [ "$portainer" = 1 ]; then
    how="update the stack within 30 days of that date: rag-bootstrap re-issues it and says what to restart (docs/portainer.md)"
  elif [ "$file" = ca.pem ]; then
    how="init-deployment.sh --renew-ca with ca.key back on the host, then restart the stack (runbook, Secrets and certificates)"
  elif [ -e "$certs/$svc/.internal" ] || [ "$svc" = postgres ] || [ "$svc" = worker ] || [ "$svc" = parser ]; then
    how="init-deployment.sh --renew-certs with ca.key back on the host, then restart postgres and rag-parser (runbook, Secrets and certificates)"
  else
    how="your PKI's certificate: re-run init-deployment.sh with the new one (--api-cert/--admin-cert)"
  fi
  echo "warning: $file expires $(date -u -d "@$end" +%F) ($left days): $how"
done

if mkdir -p "$state_dir" 2>/dev/null && [ -w "$state_dir" ]; then
  tmp="$(mktemp "$state_dir/.certs.prom.XXXXXX")"
  cat > "$tmp" <<EOF
# HELP rag_cert_expiry_timestamp_seconds When an internal certificate expires (notAfter).
# TYPE rag_cert_expiry_timestamp_seconds gauge$metrics
# HELP rag_cert_check_timestamp_seconds When the certificates were read.
# TYPE rag_cert_check_timestamp_seconds gauge
rag_cert_check_timestamp_seconds $now
EOF
  chmod 0644 "$tmp"
  mv -f "$tmp" "$state_dir/certs.prom"
fi
exit "$warn"
