#!/usr/bin/env bash
# The local resource test (dev/resource-test/README.md): starts the AppHost stack with FakeGateway, measures memory and
# CPU per component while idle, while crawling a generated site, and under retrieval load, optionally measures the
# parser on its own, writes OUTDIR/report.md and stops everything it started.
#
# On demand and locally only: it refuses to run in CI, and no workflow calls it.
#
# Usage: dev/resource-test/run.sh [--out DIR] [--pages N] [--idle S] [--load S] [--ingest-timeout S]
#                                 [--parser-image IMAGE] [--keep]
set -euo pipefail

if [[ -n "${CI:-}" || -n "${GITHUB_ACTIONS:-}" ]]; then
  echo "run.sh: the resource test runs locally and on demand only, never in CI." >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
apphost="$root/src/Kalla.AppHost/Kalla.AppHost.csproj"
out="" pages=150 idle=60 load=20 ingest_timeout=900 parser_image="" keep=false
site_port=7418 parser_port=7419 parser_container=ume-rag-restest-parser

while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) out="$2"; shift 2 ;;
    --pages) pages="$2"; shift 2 ;;
    --idle) idle="$2"; shift 2 ;;
    --load) load="$2"; shift 2 ;;
    --ingest-timeout) ingest_timeout="$2"; shift 2 ;;
    --parser-image) parser_image="$2"; shift 2 ;;
    --keep) keep=true; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "run.sh: unknown argument $1" >&2; exit 2 ;;
  esac
done

for tool in aspire docker dotnet python3 curl; do
  command -v "$tool" >/dev/null || { echo "run.sh: $tool is not on PATH" >&2; exit 2; }
done
if aspire ps --non-interactive 2>/dev/null | grep -q "Kalla.AppHost"; then
  echo "run.sh: an AppHost is already running; stop it first (aspire stop --apphost $apphost)." >&2
  exit 2
fi
if curl -s -o /dev/null "http://127.0.0.1:$site_port/"; then
  echo "run.sh: port $site_port is in use." >&2
  exit 2
fi

out="${out:-$(mktemp -d "${TMPDIR:-/tmp}/ume-rag-resource-test.XXXXXX")}"
mkdir -p "$out"
samples="$out/samples.tsv" facts="$out/facts.txt"
: > "$samples"; : > "$facts"
key_file="$(umask 077; mktemp "${TMPDIR:-/tmp}/ume-rag-restest-key.XXXXXX")"
fact() { printf '%s\t%s\n' "$1" "$2" >> "$facts"; echo "  $1: $2"; }
log() { echo "[$(date +%T)] $*"; }

fact "date" "$(date -Iseconds)"
fact "commit" "$(git -C "$root" rev-parse --short HEAD)"
fact "host" "$(nproc) CPUs, $(awk '/MemTotal/{printf "%.0f GB", $2/1048576}' /proc/meminfo) RAM"
fact "build" "AppHost (Debug .NET builds, stock Postgres settings), FakeGateway; no parser, renderer or Open WebUI in the stack"

site_pid="" pg="" original_crawl=""
cleanup() {
  set +e
  rm -f "$key_file"
  [[ -n "$site_pid" ]] && kill "$site_pid" 2>/dev/null
  docker rm -f "$parser_container" >/dev/null 2>&1
  if [[ -n "$original_crawl" && -n "$pg" ]]; then
    psql_q "update config.\"Settings\" set \"Crawl\" = '${original_crawl//\'/\'\'}'::jsonb" >/dev/null && log "Settings.Crawl restored"
  fi
  if [[ "$keep" != true ]]; then
    aspire stop --apphost "$apphost" --non-interactive >/dev/null 2>&1 && log "AppHost stopped"
  fi
  log "report: $out/report.md"
}
trap cleanup EXIT

secrets="$(dotnet user-secrets --project "$root/src/Kalla.AppHost" list)"
secret() { sed -n "s/^Parameters:$1 = //p" <<< "$secrets"; }
pg_password="$(secret postgres-password)"
[[ -n "$pg_password" ]] || { echo "run.sh: no Parameters:postgres-password in the AppHost's user secrets (run the AppHost once)" >&2; exit 2; }
psql_q() { docker exec -e PGPASSWORD="$pg_password" "$pg" psql -U postgres -d ragdb -Atc "$1"; }

# 1. The stack: FakeGateway, Postgres, Keycloak, ClamAV, the services; no parser, renderer or Open WebUI (each would
# build or pull gigabytes). E2E mode lets the worker crawl loopback, where the generated site is served.
log "starting the AppHost (parser, renderer and Open WebUI off; E2E mode for loopback crawling)"
Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false E2E__Enabled=true \
  aspire start --apphost "$apphost" --non-interactive >/dev/null
for resource in postgres api adminapi worker clamav; do
  aspire wait "$resource" --status healthy --timeout 600 --apphost "$apphost" --non-interactive >/dev/null
done
pg="$(docker ps --format '{{.Names}}' | grep -m1 '^postgres-')"

# 2. Allow the site's port for the crawl (Settings.Crawl.allowedPorts; restored on exit) and restart the worker,
# which reads it at start.
original_crawl="$(psql_q 'select coalesce("Crawl", '\''{}'\''::jsonb)::text from config."Settings"')"
psql_q "update config.\"Settings\" set \"Crawl\" = jsonb_set(coalesce(\"Crawl\", '{}'::jsonb), '{allowedPorts}',
  (select coalesce(jsonb_agg(distinct p), '[]'::jsonb) from (select jsonb_array_elements(coalesce(\"Crawl\"->'allowedPorts', '[]'::jsonb)) p
   union select to_jsonb($site_port)) x))" >/dev/null
aspire resource worker restart --apphost "$apphost" --non-interactive >/dev/null
aspire wait worker --status healthy --timeout 300 --apphost "$apphost" --non-interactive >/dev/null
db_before="$(psql_q "select pg_database_size('ragdb')")"

log "idle for $idle s"
python3 -I "$here/sample.py" idle "$idle" "$samples"

# 3. Ingestion: a generated Swedish site (benchmarks/release-test/site, HTML only: no parser), a URL-list source on a new
# collection, and the sync the seed enqueues.
log "generating $pages pages and seeding a URL-list source"
python3 -I "$root/benchmarks/release-test/site/build_site.py" generate --out "$out/site" --pages "$pages" --pdf-every 0 --ocr 0 \
  --base-url "http://127.0.0.1:$site_port" >/dev/null
grep -o '<loc>[^<]*</loc>' "$out/site/sitemap.xml" | sed 's/<[^>]*>//g' > "$out/urls.txt"
python3 -I -m http.server "$site_port" --bind 127.0.0.1 --directory "$out/site" >/dev/null 2>&1 &
site_pid=$!
slug="restest-$(date +%Y%m%d-%H%M%S)"
seeded="$(UME_RAG_DEV_DB="Host=localhost;Port=7412;Username=postgres;Password=$pg_password;Database=ragdb" \
  UME_RAG_DEV_KEY_PEPPER="$(secret key-pepper)" \
  python3 -I "$root/scripts/dev/seed_url_list.py" --profile dev-embedding --urls-file "$out/urls.txt" --api-key \
    --collection-slug "$slug" --docker "$pg")"
grep -o 'ume-rag-sk-[A-Za-z0-9]*' <<< "$seeded" > "$key_file"
collection="$(sed -n 's/^ *collection: \([0-9a-f-]*\).*/\1/p' <<< "$seeded")"
source_id="$(sed -n 's/^ *source: \([0-9a-f-]*\).*/\1/p' <<< "$seeded")"
[[ -s "$key_file" && -n "$collection" && -n "$source_id" ]] || { echo "run.sh: the seed printed no key, collection or source" >&2; exit 1; }

log "ingesting (until the job queue is empty, at most $ingest_timeout s)"
python3 -I "$here/sample.py" ingest "$ingest_timeout" "$samples" & sampler=$!
started=$(date +%s) idle_polls=0 seen=false
while (( $(date +%s) - started < ingest_timeout )); do
  sleep 5
  pending="$(psql_q "select count(*) from content.\"Jobs\" where \"State\" in ('queued','running')")"
  if [[ "$pending" == 0 ]]; then idle_polls=$((idle_polls + 1)); else idle_polls=0 seen=true; fi
  # Done when the queue was busy and is now empty twice in a row (or empty for a minute: nothing was enqueued).
  if (( idle_polls >= 2 )) && { [[ "$seen" == true ]] || (( $(date +%s) - started >= 60 )); }; then break; fi
done
ingest_seconds=$(( $(date +%s) - started ))
kill "$sampler" 2>/dev/null; wait "$sampler" 2>/dev/null || true
docs="$(psql_q "select count(*) from content.\"Documents\" where \"SourceId\" = '$source_id' and \"DeletedAt\" is null")"
chunks="$(psql_q "select count(*) from content.\"Chunks\" where \"CollectionId\" = '$collection' and \"DeletedAt\" is null")"

# 4. Retrieval load: 1 client, then 8, against /v1/retrieve (query embeddings from FakeGateway).
for clients in 1 8; do
  log "retrieval load, $clients client(s), $load s"
  python3 -I "$here/sample.py" "load-$clients" "$((load + 5))" "$samples" --interval 4 & sampler=$!
  sleep 1
  result="$(python3 -I "$here/load.py" --key-file "$key_file" --clients "$clients" --seconds "$load")"
  wait "$sampler" || true
  printf 'retrieval, %s client(s)\t%s\n' "$clients" "$result" >> "$facts"
done
db_after="$(psql_q "select pg_database_size('ragdb')")"

# 5. The parser on its own (optional): an existing image with the Compose limits, warm-up, then four fixtures.
if [[ -n "$parser_image" ]]; then
  log "parser: $parser_image (2 CPUs, 3.5 GB, 1 worker x 2 threads, /tmp on tmpfs)"
  docker run -d --name "$parser_container" --cpus 2 --memory 3.5g --read-only --tmpfs /tmp:size=1g \
    -p "127.0.0.1:$parser_port:8000" -e PARSER_WORKERS=1 -e PARSER_THREADS=2 -e PARSER_SLOT_MAX_RSS_MB=2200 \
    "$parser_image" >/dev/null
  warm_started=$(date +%s)
  until curl -sf -o /dev/null "http://127.0.0.1:$parser_port/health/ready"; do
    (( $(date +%s) - warm_started > 300 )) && { echo "run.sh: the parser did not get ready in 300 s" >&2; exit 1; }
    sleep 2
  done
  fact "parser ready after" "$(( $(date +%s) - warm_started )) s"
  python3 -I "$here/sample.py" parser 600 "$samples" --interval 2 & sampler=$!
  sleep 5  # one warm idle sample before the first conversion
  fixtures="$root/tests/fixtures/parser"
  for spec in text-layer.pdf:.pdf:text_layer text-layer.pdf:.pdf:docling scanned.pdf:.pdf:docling text.docx:.docx:docling slides.pptx:.pptx:docling; do
    IFS=: read -r file ext pipeline <<< "$spec"
    # The whole multipart response goes to a file first: a pipe closed early by grep would end curl with SIGPIPE.
    curl -s -o "$out/convert.out" -F "file=@$fixtures/$file" \
      -F "options={\"request_id\":\"restest-$RANDOM\",\"file_ext\":\"$ext\",\"pipeline\":\"$pipeline\"}" "http://127.0.0.1:$parser_port/v1/convert"
    summary="$(grep -a -m1 '^{"contract"' "$out/convert.out" \
      | python3 -I -c 'import json,sys; d=json.loads(sys.stdin.readline()); t=d.get("timings",{}); print(d["status"], "pages", d["input"].get("page_count"), "total", t.get("total_ms"), "ms")' \
      || echo "no result part (HTTP error or refusal)")"
    rm -f "$out/convert.out"
    printf 'parser %s (%s)\t%s\n' "$file" "$pipeline" "$summary" >> "$facts"
  done
  sleep 4
  kill "$sampler" 2>/dev/null; wait "$sampler" 2>/dev/null || true
fi

fact "ingestion" "$docs documents, $chunks chunks in $ingest_seconds s ($pages generated pages, HTML only)"
fact "database growth" "$(( (db_after - db_before) / 1048576 )) MB ($(( chunks > 0 ? (db_after - db_before) / chunks / 1024 : 0 )) KB per chunk)"
python3 -I "$here/report.py" "$out" >/dev/null
