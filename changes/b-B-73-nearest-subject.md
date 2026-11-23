- **Topics on related documents and the nearest subject in the chat extension** (B-73, #959): every
  `kalla.related_documents` entry now has `topics`, the topic tags of the document's best-ranked chunk as stored (`[]`
  when it has none), in the list after the text and in the progress chunk alike. A new optional member
  `kalla.nearest`, `true` or `false` (any other value is 400 `invalid_request`), adds `nearest` to the answer's `kalla`:
  for a turn that ended `not-found` or `answered-general`, `{ "collection", "topics" }` of the search's first result
  (the knowledge base's slug and that chunk's tags), and `null` for every other turn and when the search found nothing.
  It names no title, address or document id, and with `kalla.topics` it is the first result of what the narrowed links
  searched. The tags come with the hits the search already reads: no extra query.
