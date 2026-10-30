# sv_SE Hunspell word list

Used by `SwedishDecompounder` (embedded resources `sv_SE.aff`, `sv_SE.dic`). Not loaded into Postgres.

| | |
|---|---|
| Source | LibreOffice dictionaries, `sv_SE/dictionaries/sv_SE.{aff,dic}` ("Den stora svenska ordlistan", maintained by Göran Andersson, upstream release 2.42) |
| Commit | [`25d7793000bc9a149de365e170b67fe255769000`](https://github.com/LibreOffice/dictionaries/tree/25d7793000bc9a149de365e170b67fe255769000/sv_SE) (2026-07-04; the newer `4f1df64b2e8e` of 2026-10-27 is under the 7-day rule) |
| SHA-256 | `sv_SE.dic` `384a2126eff333f5f6f9790ae892554546f53948d2988c600397cb5ad6ce66e8`, `sv_SE.aff` `b721c9d44bee912feb182b601a1bc2ae3e7dffef660f4130cf2751867488a9dd` (as downloaded, LF) |
| Licence | GNU LGPL 3.0 (`LICENSE_en_US.txt`, `LICENSE_sv_SE.txt`). Whether embedding it is acceptable is open in register P2 (`plans/postponed-decisions.md`), decided before the pilot. |

Changing the list changes `SearchText` for every chunk: the worker command `reindex-fts` recomputes it (data-model §3.2), and
the release notes say so.
