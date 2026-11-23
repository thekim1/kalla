- **The nightly answer run**: every night at 04:00 each published assistant answers up to
  `Settings.EvaluationAnswerQuestions` test questions of its knowledge bases (people's questions first, then the newest
  generated ones) with its published settings, and a judge (`Settings.EvaluationJudgeModel`) rates each answer's
  faithfulness and citation precision. The calls run as the assistant's gateway key and are counted under the test
  panel's client, as the steps *Nightly answer evaluation* and *Nightly answer judging*. A refused key or a spent
  budget ends that assistant's run for the night. The assistant's *Statistics* tab shows the last run's figures beside
  the run before and the share of chat answers without a citation. The setting is in the Settings page's *Evaluation*
  section (0 to 100 questions a night, default 100; 0 switches the run off). The last 60 runs per assistant are kept.
