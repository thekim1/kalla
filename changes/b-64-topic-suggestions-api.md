- **Topic suggestions** (B-64, part 1): `GET /api/collections/{id}/topic-suggestions` lists, per source, the sections of
  its live documents: the first path segment of the URLs (website) or the first folder of the display names (upload),
  with `under=` for one section's next level. Sections under 5 documents are left out. Each section has its document
  count, its page's title, the tag to propose and the topics whose path rules already cover it. Only the owning
  department sees them. `GET …/topics` lists a published rule's tag as `pending` while its source is processed again.
