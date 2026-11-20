#!/usr/bin/env bash
# Takes the real page copy for R1: the pages and PDFs of a URL list (2 000 umea.se URLs chosen from its sitemap), saved
# under DIR in their URL paths, then robots.txt and sitemap.xml written for the local server (build_site.py index).
# LIVE ONLY (P1b live item 22): run it once, by hand, with the web team's consent; CI never runs it. Polite: one
# request at a time, 1-2 s apart, an identifying User-Agent. The OCR batch is copied in from the generated placeholder
# or a folder of real scanned PDFs (--ocr-from).
#
#   benchmarks/release-test/site/take-copy.sh --urls FILE --out DIR [--ocr-from DIR] [--base-url URL]
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
urls=""; out=""; ocr_from=""; base_url="http://r1-site.test:8088"
while [ $# -gt 0 ]; do
  case "$1" in
    --urls) urls="$2"; shift 2;;
    --out) out="$2"; shift 2;;
    --ocr-from) ocr_from="$2"; shift 2;;
    --base-url) base_url="$2"; shift 2;;
    *) echo "unknown option: $1" >&2; exit 1;;
  esac
done
if [ ! -r "$urls" ] || [ -z "$out" ]; then sed -n '2,8p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 1; fi
command -v wget >/dev/null || { echo "error: wget is required" >&2; exit 1; }
mkdir -p "$out"
# --no-host-directories keeps the URL path as the file path; --adjust-extension gives HTML pages a .html name;
# --convert-links makes links between copied pages relative, so the crawl stays on the local server.
wget --input-file="$urls" --directory-prefix="$out" --force-directories --no-host-directories --adjust-extension \
  --convert-links --wait=1 --random-wait --tries=2 --timeout=30 \
  --user-agent="Källa R1 page copy (Umea kommun platform team)" --no-verbose
if [ -n "$ocr_from" ]; then
  mkdir -p "$out/ocr-batch" && cp "$ocr_from"/*.pdf "$out/ocr-batch/"
fi
python3 -I "$here/build_site.py" index --out "$out" --base-url "$base_url"
