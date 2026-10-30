#!/usr/bin/env bash
# Host firewall for the Compose networks (plans/platform/repo-and-deployment.md §6.1): egress per network in Docker's
# DOCKER-USER chain, and no new connections from any of them to the host itself. Idempotent: it rebuilds its own two
# chains in one iptables-restore transaction and jumps to them from DOCKER-USER and INPUT once. rag-firewall.service
# runs it after Docker starts and every 15 minutes (the allow-lists are host names, resolved when it runs).
#
#   deploy/host/docker-user-firewall.sh [--dry-run] [--env-file FILE] [--subnet NAME=CIDR]... [--allow NAME=HOST[:PORT]]...
#   deploy/host/docker-user-firewall.sh --remove | --print-subnets
#
# A container sends all its egress through the network that holds its default route: Compose pins it with gw_priority
# (rag-api and rag-admin: llm; rag-worker: crawl; postgres, rag-clamav and Open WebUI have one non-internal network),
# so each allow-list below is everything the members of that network call.
#   edge       none: it carries the published ports only (replies pass as established connections)
#   data, ingestion, parser
#              none (internal networks; dropped here as well)
#   llm        the gateway (UME_RAG_GATEWAY_URL), the OIDC authority (UME_RAG_OIDC_AUTHORITY), the Open WebUI host
#              (UME_RAG_OPENWEBUI_HOST: the feedback import), the OTLP endpoint (UME_RAG_OTEL_ENDPOINT), DNS
#   crawl      the gateway, the OTLP endpoint, DNS and UME_RAG_CRAWL_ALLOW; then nothing private (RFC 1918, CGNAT,
#              loopback, link-local, documentation, multicast and reserved ranges), the rest of the Internet allowed
#   mirror     database.clamav.net:443, or only freshclam's proxy (UME_RAG_FRESHCLAM_PROXY); DNS
#   backup     the S3 endpoint (UME_RAG_BACKUP_ENDPOINT) and DNS; none with a mounted share
#   openwebui  with UME_RAG_OPENWEBUI_HOST set: rag-api's public URL (UME_RAG_API_PUBLIC_URL, through the proxy), the
#              OIDC provider (UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL), DNS; else none
# DNS: the upstream resolvers Docker's embedded DNS forwards to from the container (the non-loopback nameservers of
# /etc/resolv.conf, or of /run/systemd/resolve/resolv.conf behind systemd-resolved); --dns IP replaces them.
#
# Options:
#   --env-file FILE        [deploy/.env] the values above
#   --dry-run              print the rules instead of applying them (no root needed)
#   --subnet NAME=CIDR     another subnet for a network (they must match the Compose files; CI checks the defaults)
#   --allow NAME=HOST[:PORT]  one more destination for a network (port 443 when omitted)
#   --dns IP               a resolver (repeat the option)
#   --remove               remove the chains and jumps
#   --print-subnets        print "NAME CIDR" per network and exit
# IPv4 only: the networks have no IPv6. Run as root with the iptables variant Docker uses (iptables-nft on current
# distributions); it needs Docker's iptables integration (DOCKER-USER), not the nftables firewall backend.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="$here/../.env"; dry_run=0; remove=0; print_subnets=0
dns_servers=(); extra=()
chain=UME-RAG-EGRESS; input_chain=UME-RAG-INPUT
reject=(-j REJECT --reject-with icmp-admin-prohibited)

# Networks in the order the rules are written, with the subnets of compose.prod.yaml, compose.ingestion.yaml and
# compose.openwebui.yaml.
names=(edge data llm ingestion parser crawl mirror backup openwebui)
declare -A subnet=(
  [edge]=172.30.1.0/24 [data]=172.30.2.0/24 [llm]=172.30.3.0/24 [ingestion]=172.30.4.0/24 [parser]=172.30.5.0/24
  [crawl]=172.30.6.0/24 [mirror]=172.30.7.0/24 [backup]=172.30.16.0/24 [openwebui]=172.30.17.0/24
)
private=(0.0.0.0/8 10.0.0.0/8 100.64.0.0/10 127.0.0.0/8 169.254.0.0/16 172.16.0.0/12 192.0.0.0/24 192.0.2.0/24
  192.168.0.0/16 198.18.0.0/15 198.51.100.0/24 203.0.113.0/24 224.0.0.0/4 240.0.0.0/4)

usage() { sed -n '2,36p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }
die() { echo "error: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) env_file="${2:-}"; shift 2;;
    --dry-run) dry_run=1; shift;;
    --subnet)
      n="${2%%=*}"; [ -n "${subnet[$n]+x}" ] || die "--subnet: unknown network '$n'"
      subnet[$n]="${2#*=}"; shift 2;;
    --allow)
      n="${2%%=*}"; [ -n "${subnet[$n]+x}" ] || die "--allow: unknown network '$n'"
      extra+=("$2"); shift 2;;
    --dns) dns_servers+=("${2:-}"); shift 2;;
    --remove) remove=1; shift;;
    --print-subnets) print_subnets=1; shift;;
    -h|--help) usage 0;;
    *) echo "unknown option: $1" >&2; usage 1;;
  esac
done

if [ "$print_subnets" = 1 ]; then
  for n in "${names[@]}"; do echo "$n ${subnet[$n]}"; done
  exit 0
fi

ensure_jump() { # ensure_jump FROM TO — one jump at the top of FROM
  if [ "$dry_run" = 1 ]; then echo "iptables -C $1 -j $2 || iptables -I $1 1 -j $2"; return; fi
  iptables -C "$1" -j "$2" 2>/dev/null || iptables -I "$1" 1 -j "$2"
}

if [ "$remove" = 1 ]; then
  for pair in "DOCKER-USER $chain" "INPUT $input_chain"; do
    read -r from to <<<"$pair"
    if [ "$dry_run" = 1 ]; then echo "iptables -D $from -j $to; iptables -F $to; iptables -X $to"; continue; fi
    while iptables -D "$from" -j "$to" 2>/dev/null; do :; done
    iptables -F "$to" 2>/dev/null || true
    iptables -X "$to" 2>/dev/null || true
  done
  exit 0
fi

if [ "$dry_run" = 0 ]; then
  [ "$(id -u)" = 0 ] || die "run as root (or --dry-run)"
  iptables -n -L DOCKER-USER >/dev/null 2>&1 || die "no DOCKER-USER chain: start Docker (with its iptables integration) first"
fi

env_value() { sed -n "s/^$1=//p" "$env_file" 2>/dev/null | tail -n 1; }
if [ ! -r "$env_file" ]; then echo "warning: cannot read $env_file; only --allow destinations are used" >&2; fi

# endpoint URL-OR-HOST[:PORT] DEFAULT-PORT — prints "HOST PORT" (scheme, path and user info dropped).
endpoint() {
  local s="$1" port="$2" host
  case "$s" in http://*) port=80;; esac
  s="${s#*://}"; s="${s%%/*}"; s="${s##*@}"
  host="${s%%:*}"
  [ "$host" = "$s" ] || port="${s##*:}"
  echo "$host $port"
}
# resolve HOST — sets ips to its IPv4 addresses (in this shell, so a failure stops the script).
resolve() {
  if [[ "$1" =~ ^[0-9]+(\.[0-9]+){3}(/[0-9]+)?$ ]]; then ips=("$1"); return; fi
  mapfile -t ips < <(getent ahostsv4 "$1" | awk '{print $1}' | sort -u)
  [ ${#ips[@]} -gt 0 ] || die "cannot resolve $1"
}

if [ ${#dns_servers[@]} -eq 0 ]; then
  conf=/etc/resolv.conf
  if grep -q '^nameserver 127\.0\.0\.53' "$conf" 2>/dev/null && [ -r /run/systemd/resolve/resolv.conf ]; then
    conf=/run/systemd/resolve/resolv.conf
  fi
  mapfile -t dns_servers < <(awk '$1 == "nameserver" && $2 ~ /^[0-9.]+$/ && $2 !~ /^127\./ {print $2}' "$conf" | sort -u)
fi

declare -A allows=()  # network -> "HOST PORT" lines
add() { # add NETWORK URL-OR-HOST[:PORT] — nothing when the value is empty
  [ -n "$2" ] || return 0
  allows[$1]+="$(endpoint "$2" 443)"$'\n'
}
gateway="$(env_value UME_RAG_GATEWAY_URL)"; otel="$(env_value UME_RAG_OTEL_ENDPOINT)"
add llm "$gateway"; add llm "$(env_value UME_RAG_OIDC_AUTHORITY)"; add llm "$(env_value UME_RAG_OPENWEBUI_HOST)"
add llm "$otel"
add crawl "$gateway"; add crawl "$otel"
proxy="$(env_value UME_RAG_FRESHCLAM_PROXY)"
if [ -n "$proxy" ]; then add mirror "$proxy"; else add mirror database.clamav.net; fi
add backup "$(env_value UME_RAG_BACKUP_ENDPOINT)"
if [ -n "$(env_value UME_RAG_OPENWEBUI_HOST)" ]; then
  add openwebui "$(env_value UME_RAG_API_PUBLIC_URL)"; add openwebui "$(env_value UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL)"
fi
for spec in ${extra[@]+"${extra[@]}"}; do add "${spec%%=*}" "${spec#*=}"; done

# The ruleset, one iptables-restore transaction: DOCKER-USER sees forwarded traffic (a container to anywhere but the
# host), INPUT traffic to the host's own addresses.
out=("*filter" ":$chain - [0:0]" ":$input_chain - [0:0]" "-A $chain -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN")
for n in "${names[@]}"; do
  s="${subnet[$n]}"
  out+=("# $n" "-A $chain -s $s -d $s -j RETURN")
  if [ -n "${allows[$n]:-}" ] || [ "$n" = crawl ]; then
    for ip in ${dns_servers[@]+"${dns_servers[@]}"}; do
      out+=("-A $chain -s $s -d $ip -p udp --dport 53 -j RETURN" "-A $chain -s $s -d $ip -p tcp --dport 53 -j RETURN")
    done
  fi
  while read -r host port; do
    [ -n "$host" ] || continue
    resolve "$host"
    for ip in "${ips[@]}"; do out+=("-A $chain -s $s -d $ip -p tcp --dport $port -j RETURN"); done
  done <<<"${allows[$n]:-}"
  if [ "$n" = crawl ]; then
    for cidr in $(env_value UME_RAG_CRAWL_ALLOW | tr ',' ' '); do out+=("-A $chain -s $s -d $cidr -j RETURN"); done
    for cidr in "${private[@]}"; do out+=("-A $chain -s $s -d $cidr ${reject[*]}"); done
    out+=("-A $chain -s $s -j RETURN")
  else
    out+=("-A $chain -s $s ${reject[*]}")
  fi
done
out+=("# no new connections to the host" "-A $input_chain -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN")
for n in "${names[@]}"; do out+=("-A $input_chain -s ${subnet[$n]} ${reject[*]}"); done
out+=("COMMIT")

if [ "$dry_run" = 1 ]; then
  echo "iptables-restore --noflush <<EOF"
  printf '%s\n' "${out[@]}"
  echo "EOF"
else
  printf '%s\n' "${out[@]}" | iptables-restore --noflush
fi
ensure_jump DOCKER-USER "$chain"
ensure_jump INPUT "$input_chain"
