- **The nightly answer evaluation is on after the upgrade.** From the first night every published, active assistant
  whose knowledge bases have test questions answers up to 100 of them, and the judge model rates each answer: up to 100
  answers and 100 judge calls per assistant and night, charged to the assistant's gateway key. Nothing runs while no
  judge model is set. To run fewer, or none, set *Questions per assistant and night*, or switch *Run the nightly answer
  evaluation* off, in *Settings › Evaluation* (`Settings.EvaluationAnswerQuestions`, 0 = off).
