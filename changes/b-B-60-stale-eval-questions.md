- **The admin shows evaluation questions whose expected documents were deleted** (#893): a human test question whose
  expected document was deleted says so in the questions table, with a shortcut from a notice above it; the editor
  marks the deleted document, so it is replaced or removed before saving, or the question is retired. The report card
  shows the run's `documentGone` ("N questions left out: their documents were deleted"), read from the run's job
  result; a run whose job is past the 7-day job retention shows no count.
