- **Cost per outcome** (B-61): every assistant turn now adds one turn and its full cost (all its model calls, the
  gateway's cost and the estimate apart) to a daily row per outcome, `chat."AssistantDailyOutcomes"`, kept 3 years. A
  turn that fails after a model call counts as *Error* or *Unavailable*; test-panel and test-question turns count under
  the test panel's client. `GET /api/assistants/{id}/costs` returns them as `outcomes`, and the assistant's *Cost* card
  on the Statistics tab shows the turns, the cost and the cost per turn by outcome, real use and the test panel apart.
