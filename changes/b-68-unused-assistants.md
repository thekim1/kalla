- **Unused assistants** (B-68): Home's *Needs attention* lists a published assistant nobody has asked anything in 90
  days (published at least 90 days ago), with when it was last asked. **Archive** hides it from every client after a
  confirmation, as on its page; **Keep** keeps it off the list for 90 days (`POST /api/assistants/{id}/keep`, audited
  `assistant.keep`). Health's *By department* is a larger touch target on a phone.
