- **A source's own schedule, and when it next runs**: the URL-list and website forms have *Fetch again*: the platform
  default for the type, every night at 02:00, every Saturday night at 01:00, or a cron expression of its own (Swedish
  time). The source page shows the schedule in force and the next scheduled fetch (the per-source spread included), or
  that the source is paused. `GET /api/sources/{id}` answers `scheduleInForce` and `nextRunAt`.
