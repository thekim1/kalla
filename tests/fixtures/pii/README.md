# Personal-data fixtures (synthetic only)

Used by `tests/Kalla.Worker.Tests/Processing/PiiTargetTests.cs` (processing design §17, PII precision/recall).

| File | Content | Source | Licence |
|---|---|---|---|
| `testpersonnummer.txt` | 360 test personnummer (12 digits, born 1890–2022), 40 rows from each of nine offsets | Skatteverket, "Testpersonnummer", rowstore dataset `b4de7df7-63c0-4e7e-bb59-1f156a591763` (43,895 rows; fetched 2026-10-31) | CC0 1.0 (dataportal.se entry `skatteverket.entryscape.net/store/9/resource/147`) |
| `testsamordningsnummer.txt` | 120 test samordningsnummer (12 digits, day + 60) | Skatteverket, "Testsamordningsnummer", rowstore dataset `9f29fe09-4dbc-4d2f-848f-7cffdd075383` (4,088 rows; fetched 2026-10-31) | CC0 1.0 (`store/9/resource/153`) |

Test numbers may only be used in test environments (Skatteverket); they are not real people. Every other value in the
PII tests (organisationsnummer, OCR references, phone numbers, e-mail addresses, names) is generated in the test from a
fixed seed. SCB name statistics for the NER prefilter's gazetteer are not used yet (licence not checked, to-verify #111).

`ner-names.jsonl`: 70 synthetic Swedish sentences, 55 with their gold person names (66 names: running text, hyphenated,
`von`/`af` particles, genitive, ALL CAPS, table rows) and 15 sentences without persons (places and streets named after
people, surnames that are nouns). Written for this repository; the names are common Swedish names, not real people in
these roles. Used by `tests/Kalla.ContractTests/NerTargetTests.cs` against the parser image (processing design §17
NER targets).
