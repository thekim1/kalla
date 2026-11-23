- **Feedback on documents** (B-66): every rating of an answer, from `POST /v1/feedback` or the Open WebUI import, now
  also counts once for each document the answer cited, however many of its passages were cited, in
  `chat."DocumentDailyFeedback"` (kept 3 years), in the rating's own transaction; a changed rating moves one count, and
  no count goes below 0. No question, answer or comment is stored with it, so it works in history mode `none`. `GET
  /api/collections/{id}/feedback` (with `format=csv`) lists a knowledge base's documents by the downvotes of the answers
  that cited them, summed over every assistant that cites them and only for the owning department; the knowledge base's
  Overview shows them on a new *Feedback on documents* card, whose *Show the ratings* opens the Feedback page filtered
  by the document (`documentId`). A document cited in 3 or more downvoted answers in the last 7 days is a new item in
  Home's *Needs attention*.
