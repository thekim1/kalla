- **A paused source's page** (#1007): *Resume* is the primary action, and *Fetch now* and *Continue* are no longer
  shown (the API refuses both while the source is paused). A banner says the source is not fetched on schedule and that
  a fetch already running finishes; the earlier text said *Fetch now* still worked, which was wrong.
- **An upload source can't be paused** (#1007): it has no schedule, and a paused upload source took a batch whose files
  were then never processed. `POST /api/sources/{id}/pause` answers 409 `source_not_pausable` for one, and its page
  shows no *Pause*.
- **A paused upload source takes no files** (#1024): one paused on 0.10.0 answers a new upload batch and a batch's
  commit with 409 `source_paused` until it is resumed, instead of taking files the worker then never processes.
