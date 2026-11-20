- **Narrow a chat turn by topic** (F8): a client other than Open WebUI may send `ume.topics` (1–20 topics in the
  tag format) to search only chunks with one of those topics, inside what the assistant's links allow: a link with
  topics keeps the common ones and is left out when none is, a link without topics is searched for the asked ones.
  The answer's `ume.topics` lists the asked topics a link searched; a turn with no link left ends `not-found`
  without a search. Open WebUI's requests ignore it.
