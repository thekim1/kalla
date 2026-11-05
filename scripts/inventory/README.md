# Pilot-start source inventory (P1a item 1)

`inventory.py` does the pilot-start checks of P1a item 1 (`plans/archive/p1/README.md` §3; to-verify #139; postponed Z1):
what formats a pilot site's sitemaps list, how common the Z1 formats are (legacy Office, RTF, e-mail, deferred in
P1), how complete and how old the sitemap `lastmod` values are, and whether the site's raw HTML carries its main text
without JavaScript (connectors design §8.1: the renderer is built in P2 only if a pilot site needs it).

It is one Python file using only the standard library (Python 3.12+), like `scripts/capacity/` and `scripts/grading/`,
so nothing is pinned or installed. Tests: `test_inventory.py` (standard library `unittest`; fixture sitemaps and pages
in `tests/fixtures/inventory/`, an in-process fake site for the fetcher). Lint: the parser's ruff rules and pin
(`ruff.toml`).

```bash
python3 -I -m unittest discover -s scripts/inventory -v
uvx ruff@0.17.2 check --config scripts/inventory/ruff.toml scripts/inventory
uvx ruff@0.17.2 format --check --config scripts/inventory/ruff.toml scripts/inventory
```

## `sitemap`: formats, Z1 share, `lastmod`, hosts

```bash
python3 -I scripts/inventory/inventory.py sitemap https://www.umea.se/robots.txt \
  --pages tests/fixtures/umea/pages/*.html
```

The source is a sitemap or sitemap index URL, a site's `robots.txt` URL (its `Sitemap:` lines are the indexes), or a
local file. A local index reads its children from its own directory, by the last path segment of each child's `loc`,
so a saved run can be recomputed offline:

```bash
python3 -I scripts/inventory/inventory.py sitemap scripts/inventory/results/<time>-sitemaps/sitemapindex.xml \
  --now <the run's time>
```

Sitemaps are read as the worker's `SitemapParser` reads them (connectors design §5.1): gzip by its magic bytes, at
most 50 MB uncompressed and 50,000 entries per file, DTDs refused (no entity expansion), any namespace, index depth
≤ 2 (an index at depth 2 is reported, its children are not read). Only `loc` and `lastmod` directly below `<url>` or
`<sitemap>` are taken; image and video sitemap extensions (`image:image`, `video:video`) and their nested `…loc`
elements are counted per file.

Each distinct URL is classified by the extension of its last path segment, as `CrawlScope.TypesByExtension` maps them:

| Class | Extensions |
|---|---|
| HTML | `html`, `htm`, `xhtml`, `php`, `asp`, `aspx`, `jsp`, and no extension (the worker admits it and the content type decides), except below a file-archive path |
| PDF | `pdf` |
| Modern Office | `docx`, `xlsx`, `pptx` |
| ODF | `odt`, `ods`, `odp` |
| Text | `txt`, `md`, `csv` |
| Images | `jpg`, `jpeg`, `jfif`, `png`, `gif`, `webp`, `svg`, `bmp`, `tif`, `tiff`, `ico`, `avif`, `heic` |
| Video | `mp4`, `webm`, `mov`, `avi`, `mkv`, `m4v`, `mpg`, `mpeg`, `wmv` |
| Legacy Office, RTF, e-mail (Z1 deferred) | `doc`, `xls`, `ppt`, `rtf`, `eml`, `msg` |
| Macro-enabled Office (Z1 unsupported) | `docm`, `xlsm`, `pptm` |
| Archives (Z1 unsupported) | `zip`, `7z`, `rar`, `tar`, `gz`, `tgz`, `bz2`, `xz` |
| Other | everything else, and a segment without an extension below Sitevision's file archives (`/images/`, `/download/`, `/webdav/files/`) |

The Z1 share is given of all URLs and of the non-HTML documents (PDF, Office, ODF, text and the Z1 classes). `lastmod`
ages are relative to `--now` (default: the run's start), per class, in buckets (future, ≤ 30 d, 31–90 d, 91–365 d,
1–2 y, 2–5 y, > 5 y, invalid, missing) with the median age.

## `js-need`: raw text yield

```bash
python3 -I scripts/inventory/inventory.py js-need tests/fixtures/umea/pages/*.html
python3 -I scripts/inventory/inventory.py js-need https://<host>/<page> ...
```

For each page (a local HTML file or a URL) the visible text of the raw HTML is extracted without running JavaScript,
skipping what the worker's `HtmlExtractor` drops: `script`, `style`, `noscript`, `template`, `svg`, `iframe`,
`object`, form controls, `nav`, and hidden or screen-reader-only elements (`hidden`, `aria-hidden="true"`, `sr-only`
and the like). The main root is the generic preset's: the first `main`, else `article`, else `[role=main]`, else
`body`. A page whose main text is shorter than `--min-main-chars` (200) is flagged **may need JavaScript**. Hints are
listed but never flag a page: an empty mount point (`#app`, `#root`, `#__next`, …), `noscript` text naming
JavaScript (Sitevision prints a "JavaScript inaktiverat" banner on every page), scripts ≥ 50 % of the HTML, framework
markers (Next.js, Nuxt, Angular, React, Vue). A site-specific root (such as the Sitevision preset's `.pagecontent`) is
not used, so a landing page without `main` is measured on its first `article`.

## Network use

`UmeaKommunBot/1.0 (inventory)`, one request at a time, at least `--delay` seconds (≥ 2) from the end of one request to
the start of the next (a larger `Crawl-delay` is honoured, capped at 60 s), no cookies, `Accept-Encoding: identity`.
`robots.txt` is fetched first for each origin and read as the worker's `RobotsTxt` does (RFC 9309: the
`UmeaKommunBot` groups, else `*`; longest match, `Allow` on ties; `*` and `$`; 2xx parsed, 4xx allows all, 429, 5xx or
unreachable disallows all); a disallowed URL is logged and not requested; redirects (at most 5) are re-checked. The
`sitemap` command requests only `robots.txt` and sitemap files, never pages.

## Output

Each run writes `scripts/inventory/results/<UTC time>-<command>.md` (the report, with the robots result and the
request log: URL, start time, status, bytes, duration) and `.json` (git-ignored; `--out-dir` to change). A `sitemap`
run over the network also saves the fetched sitemap files to `results/<UTC time>-sitemaps/` (`--no-save` to skip).
Exit 0 when done and no page is flagged, 1 when a page is flagged, 2 when nothing could be read (missing file, robots
disallowing, unreachable).
