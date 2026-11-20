# sv_SE Hunspell word list

Used by `SwedishDecompounder`. Not loaded into Postgres. The build copies this directory to `dictionaries/sv_SE/` next to
every application that uses `Kalla.Retrieval` (worker, API), so it is in every .NET image as separate files;
`Text__SwedishWordListDirectory` names another directory with `sv_SE.aff` and `sv_SE.dic`.

| | |
|---|---|
| Source | LibreOffice dictionaries, `sv_SE/dictionaries/sv_SE.{aff,dic}` ("Den stora svenska ordlistan", maintained by Göran Andersson, upstream release 2.42) |
| Commit | [`25d7793000bc9a149de365e170b67fe255769000`](https://github.com/LibreOffice/dictionaries/tree/25d7793000bc9a149de365e170b67fe255769000/sv_SE) (2026-07-04; the newer `4f1df64b2e8e` of 2026-10-27 is under the 7-day rule) |
| SHA-256 | `sv_SE.dic` `384a2126eff333f5f6f9790ae892554546f53948d2988c600397cb5ad6ce66e8`, `sv_SE.aff` `b721c9d44bee912feb182b601a1bc2ae3e7dffef660f4130cf2751867488a9dd` (as downloaded, LF) |
| Licence | **GNU LGPL 3.0 only** (`LICENSE_en_US.txt`, `LICENSE_sv_SE.txt`: copyright © 2003-19 Göran Andersson). The texts of the LGPL-3.0 (`COPYING.LESSER`) and the GPL-3.0 it builds on (`COPYING`) are here, from gnu.org. Ume.Rag (EUPL-1.2) uses the list unmodified and reads it with its own parser (no Hunspell code). It ships as separate files the user can replace (LGPL-3 §4 d1, #52, to-verify #113). |

Changing the list changes `SearchText` for every chunk: the worker command `reindex-fts` recomputes it (data-model §3.2), and
the release notes say so.
