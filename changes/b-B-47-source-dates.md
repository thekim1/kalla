- **Source dates on every citation** (#857, F4): a citation's label reads "(2026-03-02, kontrollerad 2026-11-18)": the
  date the source states, then when a crawl last confirmed the page, in the turn's language. A crawled page that states
  no date no longer shows its fetch time as its change date. The Knowledge API's `document` and each
  `kalla.related_documents` entry carry `modified` and the new `checked`; an upload has `modified` (its upload time when
  the file states none) and no `checked`.
