# Gold-set grading for the go-live gate (P1b #16, hand-off L8)

`grading.py` produces the file that department reviewers grade by hand before the pilot goes live, and computes the
gate's totals from the graded file (weakness review §2 "Hand grading"; ingestion plan §7; P1b README §1.1, item 31).
P1 has no grading UI: reviewers grade in a spreadsheet, and the graded file and its totals are attached to the go-live
decision.

It is one Python file using only the standard library (Python 3.12+), like `scripts/capacity/`, so nothing is pinned
or installed and it can be copied to any host that reaches the chat API. Tests: `test_grading.py` (standard library
`unittest`, an in-process fake chat API). Lint: the parser's ruff rules and pin (`ruff.toml`).

```bash
python3 -I -m unittest discover -s scripts/grading -v
uvx ruff@0.17.2 check scripts/grading && uvx ruff@0.17.2 format --check scripts/grading
```

## `answer`: ask the gold set, write the CSV

```bash
UME_RAG_API_KEY="$(cat pilot-grading.key)" \
  python3 -I scripts/grading/grading.py answer --questions gold-set.json \
    --base-url https://<api host> --assistant <pilot slug> [--not-found-text "<the assistant's not-found text>"]
```

| Option | Default | |
|---|---|---|
| `--questions` | – | the `human` evaluation questions: a JSON list, one admin keyset page `{ items, next }` of `GET /api/collections/{id}/evaluation/questions?layer=human` (admin design §4.1, §4.5), a JSON list of such pages, or a CSV (`id`, `question`, `expected_answer`, `expected_documents` separated by `\|` or new lines) |
| `--base-url` | – | the chat API (the `api` service), without `/v1` (a trailing `/v1` is accepted) |
| `--assistant` | – | the pilot assistant's slug, sent as `model` (chat-api design §3.2) |
| `--key-file` | `$UME_RAG_API_KEY` | an integration key (`kalla-sk-…`, identity design §3) of a client the pilot assistant's audience allows; read once, never printed or written, redacted from any server text |
| `--not-found-text` | the design's defaults | the assistant's own `Assistants.NotFound` text (sv and en); repeatable |
| `--no-stream` | streamed | streamed is the pilot's real path (Open WebUI streams) and gives `ttft_ms` |
| `--out` | `scripts/grading/results/<UTC time>-<slug>.csv` (git-ignored) | |
| `--delimiter` | `;` | what a Swedish Excel opens with a double click; `,` or `tab` otherwise. The file is UTF-8 with a BOM |
| `--request-timeout`, `--ca-file` | 120 s, system store | `--ca-file` for a private CA or the dev certificate (`dotnet dev-certs https --export-path dev.pem --format Pem`) |

Questions of another layer and retired questions are skipped. A question with no expected documents expects "not
found" (data-model §2.2) and is an `off-topic` row; every other question is `answerable`. Each question is sent once,
as the only `user` message of a new conversation, one after the other. 429 and 503 are retried up to three times
after `Retry-After` (at most 30 s); 401, 403 and 404 stop the run (wrong key, client or slug), keeping the rows
written so far; any other failure is recorded in the row's `error` column and the run goes on. Progress goes to stderr
(id, outcome, citation count, latency), never answer text or the key.

### The CSV (L8)

| Column | Filled by | Meaning |
|---|---|---|
| `id` | script | the question's id |
| `kind` | script | `answerable` (expected documents) or `off-topic` (expected "not found") |
| `question`, `expected_answer`, `expected_documents` | script | from the gold set: the facts a correct answer contains and its source documents |
| `answer` | script | the text the user would see, with its `[n]` markers |
| `outcome` | script | `answered`, `degraded` (keyword search only, chat-api design §10.2), `not-found`, `unavailable` (search down, never counted as not found) or `error` |
| `citations`, `citation_count` | script | one line per cited document, `[n] title - link`, numbered as in the answer |
| `latency_ms`, `ttft_ms` | script | whole answer; first content token (streamed only) |
| `assistant`, `asked_at`, `request_id`, `error` | script | for tracing a row in the API's logs |
| `grade_correct` | reviewer | answerable rows: `ja` when the answer is correct and contains the expected facts, else `nej` (a not-found answer is `nej`); off-topic rows: leave empty |
| `grade_not_found` | reviewer | every row: `ja` when the answer says it has no answer (the fixed text or the model's own words), else `nej` |
| `grade_supporting_citations` | reviewer | rows with citations: how many of the `citation_count` cited documents support what the answer says (0 to `citation_count`) |
| `reviewer_note` | reviewer | free text, not counted |

`ja`/`nej` also accepts `j`, `n`, `yes`, `no`, `x`, `1`, `0`. A cell that would start with `=`, `+`, `-` or `@` is
written with a leading `'` so the spreadsheet shows it as text instead of running it as a formula. Save the graded
sheet as CSV (UTF-8); `;`, `,` and tab are all read.

The chat API has no coverage field: `outcome` is detected from the delivered text (the not-found and search-unavailable
texts of chat-api design §10) and from an answer without citations whose usage reports no model call. It guides the
reviewers; the totals count only their `grade_not_found`, and they print how many rows the two disagree on.

## `totals`: the four go-live measures

```bash
python3 -I scripts/grading/grading.py totals graded.csv [--out totals.txt]
```

| Measure | Counted as | Passes at (default, P1b README §1.1) | Option |
|---|---|---|---|
| Correct answers on answerable questions | answerable rows with `grade_correct` ja / answerable rows | ≥ 80 % | `--min-correct 0.80` |
| Citation precision | sum of `grade_supporting_citations` / sum of `citation_count`, all rows | ≥ 90 % | `--min-citation-precision 0.90` |
| False not-found | answerable rows with `grade_not_found` ja / answerable rows | ≤ 10 % | `--max-false-not-found 0.10` |
| Off-topic answered "not found" | off-topic rows with `grade_not_found` ja / off-topic rows | ≥ 80 % | `--min-off-topic-not-found 0.80` |

The department confirms the thresholds when the set is written (P1b README §6, U-k); pass changed ones as options.
Shares are compared exactly (16/20 meets 80 %). A measure with nothing to count (no off-topic rows, no citations)
fails. Exit 0: every measure passes; 1: at least one fails; 2: the file is not completely or consistently graded (a
missing or unreadable grade, correct and not found on the same row, more supporting citations than citations), with
every such row listed. A row with a request `error` is graded as delivered; re-ask those questions before the gate.

`tests/fixtures/grading/graded-sample.csv` is a hand-made graded sample of 25 rows that meets every threshold exactly;
`graded-sample-below.csv` differs in one grade and fails "correct answers" at 15/20.

## Go-live run (live block, item 31)

1. **The gold set** (J13) is stored as `human` questions of the pilot collection. Export them: signed in to the admin,
   open `/api/collections/<id>/evaluation/questions?layer=human` and save each page's JSON (follow `next`
   with `after=`) into one JSON list, or export a CSV with the columns above.
2. **A key**: an integration client (kind `integration`, so citations come as `url_citation` annotations) in the pilot
   assistant's audience, with an `kalla-sk-` key in a file readable only by you. The answers are real chats of
   history mode `none` and count toward the assistant's statistics and cost.
3. **Run `answer`** against the pilot deployment (the published assistant version that goes live), with the
   assistant's not-found texts as `--not-found-text`. 150–200 questions take about as many answers' time (one at a
   time).
4. **Grade**: hand the CSV to the reviewers the department named (U-k). Each grading column is filled per the table
   above.
5. **Run `totals`** on the graded file with `--out`, and attach the graded CSV and the totals to the go-live
   decision. Every measure must pass.
