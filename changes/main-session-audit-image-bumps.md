- **Security updates of the parser, worker, renderer and Postgres images** (the image scan of 0.8.0): the parser
  installs from a Debian snapshot of 2026-11-19, which has LibreOffice `25.2.3-2+deb13u12` (nine High findings in
  twenty packages); the worker's Playwright is 1.65.1, whose driver runs Node 26.13.1 (seven High findings in Node
  24.22.1), and the renderer image follows on `playwright:v1.65.1-noble` with `playwright-core` 1.65.1; the Postgres
  image upgrades `zlib1g` to `1.3.1-1+deb13u1` (one High finding), since its pgvector base has no newer build.
