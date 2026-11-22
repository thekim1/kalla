- **Assistant templates: three starting points for a new assistant** (B-54): *Create assistant* has *Start from*, with
  *Allmän rådgivning* (citizen service) preselected, *Intern policy* (staff questions on internal policies, answered
  only from the documents, naming the document's owner) and *Handläggningsstöd* (step-by-step guidance from the
  routines, never deciding a case). The template's instructions, not-found texts and query steps are copied into the
  first draft, and a later change to the template leaves existing assistants alone. Emptying the field starts the
  draft from the platform's default instructions, as before. The three are seeded into an empty table at the update,
  the first with the deployment's own default instructions. Platform admins manage templates through
  `/api/assistant-templates`, and every role can read them. The migration `AssistantTemplates` adds
  `config."AssistantTemplates"`, which only `kalla-admin` reads and writes.
