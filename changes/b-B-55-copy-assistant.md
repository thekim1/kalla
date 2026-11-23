- **Copy an assistant** (B-55): the assistant page has **Copy**, which asks for a name and a department and opens the
  copy's draft. The copy starts as a draft of the source's published version, or of its draft when it was never
  published, with the same instructions, model, knowledge bases, topics, not-found texts and query steps, and the same
  description, contact, information class, history and retention. Who may use it (audiences, origins), its gateway key,
  the other versions and the statistics are not copied, and the source is unchanged. A knowledge base the target
  department can't use shows as a warning on the copy's draft. `POST /api/assistants/{id}/copy`, audited `copy`.
