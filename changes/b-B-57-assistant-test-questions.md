- **Test questions per assistant** (B-57): the assistant page has a **Test questions** tab. Add up to 30 questions people
  really ask, each with what a good answer does, and optionally a document the answer should cite and a phrase it
  should contain. **Save as test question** in *Test* starts one from the question just tried. The questions run after
  every publish and restore, and on demand with **Run test questions**, on the draft or the published version. Each is
  answered once as the test panel answers it. The expected document and phrase are checked exactly, and an AI judge
  (*Settings*, *Evaluation*, the judge model) compares the answer with what a good answer does. The report shows how
  many came out as expected, compared with the previous version, with the questions that changed and the judge's
  reasons. After a publish it offers **Keep** or **Restore the previous version**. Nothing is blocked by the result.
  Each question costs two model calls (the answer and the judgement), counted under the test panel's usage. The
  migration `AssistantTestQuestions` adds `config."AssistantTestQuestions"` (only `kalla-admin` reads and writes it),
  the result columns of `content."EvaluationResults"`, and `AssistantVersions.TestRunId`.
