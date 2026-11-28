- **An answer with no text is never `answered`** (#1106): when the answer model stops at its output limit before
  writing any text (a reasoning model that spent `max_tokens` thinking), Källa asks once more with the full output
  limit and the platform's lowest reasoning effort (`Settings.Chat.CondenseReasoningEffort`). The client sees only the
  extra time. If that answer is empty too, the turn ends with the unavailable text and outcome `unavailable`, and a
  Warning logs `answer_empty` with both calls' `max_tokens` and output tokens. An answer that wrote text and then
  stopped at the limit stays `answered`.
