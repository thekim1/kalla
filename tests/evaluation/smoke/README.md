# Evaluation smoke set

The fixture collection and the 30 questions behind the evaluation smoke check (P1 item 17; knowledge design §16.4;
repo-and-deployment §8.2).

- `documents/*.md`: 13 short Swedish pages on municipal services. **They are fictional test texts.** The numbers,
  places and rules in them are made up for the test and are not Umeå kommun's information.
- `questions.json`: 30 questions. Each lists in `expected` the documents (file names) that a correct answer cites.

## Who uses it

| Where | What |
|---|---|
| `Kalla.TestKit` `SmokeSet` | Loads both files and writes the documents into `content` as one upload collection: one chunk per paragraph, heading path `Title › Section`, FakeGateway embeddings. No worker or parser is needed. |
| `Kalla.Api.Tests` `SmokeEvaluationTests` | Asks the 30 questions through `POST /v1/retrieve` (recall@10 ≥ 0.8) and through `/v1/chat/completions` (every answer cites). |
| `verify.yml` `backend`, the evaluation smoke (stream A) | Ingests the documents through the pipeline (upload, parser, worker) and runs the same questions. |
| Evaluator == API contract test (stream A) | Compares the evaluator's results with `/v1/retrieve`'s on the seeded collection. |

With FakeGateway's feature-hashing embeddings, this checks the pipeline and the wiring, not answer quality. Those
embeddings put a question's best chunk 0.33–0.78 away (cosine distance), so the collection is created with
`SmokeSet.RetrievalSettings` (`coverageMaxDistance` 0.85) instead of the platform's BGE-M3 default of 0.50. With the
default, most questions get the not-found text.
