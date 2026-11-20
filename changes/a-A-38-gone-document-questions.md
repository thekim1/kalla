- **A deleted document no longer lowers the evaluation scores** (#878): an evaluation question whose expected document
  was deleted (removed from its source, or its source deleted) is left out of every retrieval run and counted as
  `documentGone` in the run's result instead of as a miss. A human question becomes `stale` at the run that finds it,
  until an editor points it at other documents or retires it, and turns `active` again when its document comes back
  within its 14 days. A generated question is still retired at the next top-up.
