- **An upload with a known name replaces the file** (B-83, support case SA-11): when a file added to a knowledge base's
  file list has the name and folder of a file already there (case ignored), one dialog lists those files before
  anything is sent and asks *Replace the existing files* (the default) or *Keep both*. Replacing sends each into that
  file's slot, as the row's *Replace* does, so the document keeps its id and the old version leaves the search; the
  other files upload as new. A byte-identical file still changes nothing. A new browser spec, the `tests/e2e` project
  `upload`, covers it.
