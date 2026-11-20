#!/usr/bin/env bash
# Warns before an internal certificate expires (#705): the internal CA (ca.pem, 10 years) and each service certificate
# (DIR/DIR.crt, 825 days) in the deployment's kalla-certs volume. kalla-bootstrap re-issues a service certificate only when
# it runs and fewer than 30 days remain, and Postgres's expired certificate fails every VerifyFull connection.
# rag-cert-expiry.timer runs this daily; the service fails (systemctl --failed, the journal) while a certificate
# expires within the warning window.
#
#   deploy/host/cert-expiry.sh [--days N]
#
# Options:
#   --days N            [60] warn when a certificate expires within N days
#   --env-file FILE     [deploy/.env] the deployment: its kalla-certs volume is read read-only through `docker run`
#   --certs-dir DIR     a certificate directory instead (a copy of the volume, or a Portainer host's volume directory,
#                       for example /var/lib/docker/volumes/<stack>_kalla-certs/_data)
#   --state-dir DIR     [$STATE_DIRECTORY, else /var/lib/kalla] where certs.prom goes
#
# Metrics (Prometheus text format in DIR/certs.prom, written atomically, next to backup.prom):
#   rag_cert_expiry_timestamp_seconds{file}   the certificate's notAfter
#   rag_cert_check_timestamp_seconds          when they were read
# Alert (proposed): rag_cert_expiry_timestamp_seconds - time() < 60 * 86400, or rag-cert-expiry.service failed.
set -euo pipefail
umask 027

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy_dir="$(dirname "$here")"
env_file="$deploy_dir/.env"; certs=""; days=60; state_dir="${STATE_DIRECTORY:-/var/lib/kalla}"

usage() { sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
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
if [ -z "$certs" ]; then
  # The certificates (never the keys) out of the volume, read-only, with kalla-bootstrap's image.
  [ -r "$env_file" ] || die "cannot read $env_file: pass --env-file or --certs-dir"
  dc() { docker compose --env-file "$env_file" "$@"; }
  project="$(dc config | sed -n 's/^name: //p')"
  image="$(dc config --images kalla-bootstrap)"
  [ -n "$project" ] && [ -n "$image" ] || die "cannot render the Compose project from $env_file"
  certs="$(mktemp -d)"; trap 'rm -rf "$certs"' EXIT
  docker run --rm --network none -v "${project}_kalla-certs:/c:ro" --entrypoint /bin/sh "$image" -c \
    'cd /c && find . \( -name "*.crt" -o -name ca.pem -o -name .san \) | tar -cf - -T -' | tar -xf - -C "$certs" \
    || die "cannot read the volume ${project}_kalla-certs"
fi
[ -r "$certs/ca.pem" ] || die "no readable $certs/ca.pem: run as root, or pass --certs-dir"

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
  # kalla-bootstrap writes "operator" to .san for a certificate of yours (KALLA_PKI_DIR), the host names for its own.
  if [ "$file" = ca.pem ]; then
    how="replace the internal CA (docs/production-setup.md, Internal TLS)"
  elif [ "$(cat "$certs/$svc/.san" 2>/dev/null)" = operator ]; then
    how="your PKI's certificate: put the new one in KALLA_PKI_DIR, then run kalla-bootstrap again"
  else
    how="run kalla-bootstrap within 30 days of that date (Compose: docker compose --env-file deploy/.env up -d; Portainer: update the stack): it re-issues the certificate and says what to restart"
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
