- **Compare assistant versions** (B-56): *Versions* on the assistant page has **Compare** on each version. It shows
  what differs from the draft, or from the published version when there is no draft: changed settings by name,
  knowledge bases added or removed, changed topics, and the instructions and not-found texts line by line, with added
  and removed lines marked by a sign as well as by colour. Publishing a draft that was started before a restore now
  shows that diff in its dialog (*Publish anyway*) instead of a plain question. `GET
  /api/assistants/{id}/versions/{n}/diff?against={m|draft}`, and the 409 `draft_base_changed` of publish carries `diff`.
  Texts over 20,000 characters are reported as changed without a line diff.
