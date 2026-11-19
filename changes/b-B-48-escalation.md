- **Escalation hand-off** (#857, F3): a client other than Open WebUI may add `"escalation": true` to the chat request's
  `ume`. A `not-found`, `answered-general` or `unavailable` turn then carries `ume.escalation`: the reason, the
  assistant's contact links (else the platform's) and a short summary of the user's problem for a second-line ticket.
  The summary comes from one condense-model call within `Chat:EscalationTimeout` (2 s; 0 turns it off), with links
  removed and personal identity numbers, e-mail addresses and phone numbers masked; it is never stored. An `answered`
  turn gets `"escalation": null`. The instructions are a new prompt text on *Settings → Prompt texts*.
