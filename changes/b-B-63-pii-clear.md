- **Show highlights and Clear on a document flagged for personal data** (B-63): in a knowledge base's document list
  and in a source's personal-data report, platform and department admins get **Show highlights** on a flagged
  document that is not cleared. It shows the document's passages with what the check found marked, by type, and is
  recorded in the audit log as `pii.view`. **Clear** asks why the data may be shown (at least 10 characters) and says
  what clearing does: a held version is released to the search at once, and later versions stay covered while no
  count rises. The badges and the report show the document as cleared afterwards, and
  `GET /api/sources/{id}/pii-report` gains `piiClearedAt`.
