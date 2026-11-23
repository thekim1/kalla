- **Content-gap report** (B-62): every assistant turn that ends *Not found* or *Answered from general knowledge* now
  adds one to a daily count by its nearest hit, the knowledge base and document of the search's first result (*no hit*
  when the search found nothing), in `chat."AssistantDailyGaps"`, kept 3 years. No question, answer, subject or
  conversation is stored with it, and test-panel turns are left out. `GET /api/assistants/{id}/gaps` (with
  `format=csv`) returns the period's share of such turns and the documents they came nearest to, most first (another
  department's knowledge base is named only while it is still shared with the assistant's department), and the
  assistant's Statistics tab shows them on a new *Content gaps* card.
