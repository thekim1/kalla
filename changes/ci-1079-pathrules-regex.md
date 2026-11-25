- **Path rules compile once** (#1079): a rule's regex or glob is compiled once and reused, not once per document, so
  the chunk stage and the Topics tab's match preview check more documents in the same time.
