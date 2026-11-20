# Finished queue items up to v0.9.0

The ticked items of the work queues, moved here unchanged when `v0.9.0` was released (2026-11-21).
History: it does not bind. An item is found by its ID (`B-52`); the queues in `plans/work/` hold only open and waiting
items.

## Stream A (`stream-a.md`)

### [x] A-36 A deleted source's and knowledge base's documents leave the index (#878)
- **Do:** data-model §8 and admin §4.5: when a source is deleted (and so when its knowledge base is), the worker
  soft-deletes its documents as removed ones are (`Documents.DeletedAt`, `PurgeAfter` +14 days, `DeletedAt` on the
  chunks of each live set), tombstones an upload source's blobs, and cancels its running run. **Check first what
  happens today:** the admin's `DELETE /api/sources/{id}` only sets `Sources.DeletedAt`, and the main session found
  no worker code that removes the source's documents from the index (grep, 2026-11-20; not run). If they stay
  searchable, that is a bug to say so on #878.
- **Paths:** `src/Kalla.Worker/**`, `tests/Kalla.Worker.Tests/**`.
- **Done when:** a test deletes a source with indexed documents and a search of its knowledge base finds none of
  them; the documents are purged after the 14 days and an upload source's blobs after the blob purge delay.
- **Waits on:** nothing.
- **Read:** Decisions "Deleting knowledge bases, sources and assistants"; data-model §8; connectors §12 (soft
  delete and purge); admin §4.5.

### [x] A-37 Purge documents removed from a source that still exists (#878, found in #884; #891)
- **Do:** data-model §8 and connectors §12: a document that was soft-deleted because its source no longer has it
  (`DeletedAt`, `PurgeAfter` +14 days) is deleted after `PurgeAfter` with its chunk sets and chunks, and its raw
  blobs tombstoned, as `DeletedSourcePurge` does for a deleted source's. Designed, never built (#884's design
  deviation). It meets revival (a document seen again inside the 14 days comes back, `EngineStore`), the deletion
  proposals and the evaluation questions that expect the document (A-38).
- **Paths:** `src/Kalla.Worker/**`, `tests/Kalla.Worker.Tests/**`.
- **Done when:** a test removes a document from a live source and shows it purged after the 14 days and not
  before, a revival inside them, and its raw blob tombstoned unless another document has the content.
- **Waits on:** nothing.
- **Read:** data-model §8; connectors §12; `DeletedSourcePurge`, `DeleteSourceHandler` (#884).

### [x] A-38 Evaluation questions whose expected documents are gone (#878, found in #884; #892)
- **Do:** processing §13.3 *A question whose expected document is gone*: an evaluation run leaves out every question
  with an expected document whose `DeletedAt` is set or whose row is purged, counts it as `documentGone` in the run's
  result, and sets a `human` one to `State = stale` (and back to `active` when the document is revived); `generated`
  ones keep their retirement at the top-up. The admin's line ("Its expected document was deleted", admin §7.1) is
  stream b's: open an issue for it with the run result's field.
- **Paths:** `src/Kalla.Worker/Evaluation/**`, `tests/Kalla.Worker.Tests/**`.
- **Done when:** a test deletes a source whose document a human and a generated question expect: the next run's
  recall@10 is the same as without those questions, both are counted as `documentGone`, the human one is `stale`;
  a revived document makes it `active` again.
- **Waits on:** nothing (the design answered on #889).
- **Read:** processing §13.3; admin §7.1; Decisions "Deleting knowledge bases, sources and assistants".

## Stream B (`stream-b.md`)

### [x] B-47 Source dates on every citation and related document (F4, #857)
- **Do:** knowledge §10.1: `Citation.Modified` loses the `FetchedAt` fallback for `url` documents (it stays the upload
  time for `upload`), and `Citation` gets `Checked` (`Documents.FetchedAt` for `web`, null for `file`; the hydrator
  already reads `FetchedAt`), in the Knowledge API's `document` JSON as `checked`. `Citations.Label` takes the turn's
  language and writes the dates as knowledge §10.4 says (`(2026-03-02, kontrollerad 2026-11-18)`,
  `(kontrollerad …)` without a stated date, `checked` in English), for both the Open WebUI `source` events and the
  `url_citation` titles. `RelatedDocument` gets `modified` and `checked` (chat §9.7; ISO 8601 UTC, omitted when
  null).
- **Paths:** `src/Kalla.Domain/Retrieval/**`, `src/Kalla.Retrieval/**`, `src/Kalla.Answering/**`,
  `src/Kalla.Api/**`, `src/Kalla.AdminApi/**` (only where it builds citations), `tests/**` (not
  `tests/Kalla.ContractTests/**`, `tests/Kalla.Worker.Tests/**`).
- **Done when:** chat §18.9's *Source dates* tests pass: a crawled page with and without a stated date, an English
  turn and an upload, in both the label and the related-documents entry; the Knowledge API's `document` has
  `checked` for a crawled page and none for an upload; the Open WebUI recorded fixture changes only in the label.
- **Waits on:** nothing.
- **Read:** Decisions "Source dates on citations"; knowledge §10.1, §10.4; chat §9.3, §9.7, §18.9.

### [x] B-48 Escalation hand-off: `kalla.escalation` (F3, #857)
- **Do:** chat §9.9: the request member `kalla.escalation` (§3.8, `true`/`false`, 400 otherwise); for `not-found`,
  `answered-general` and `unavailable` the object `reason`, `contacts` (the version's `NotFound.links`, else
  `Settings.Chat`'s) and `summary`, `null` for `answered` and conversational turns; the summary call (`Escalation.cs`
  beside `FollowUps.cs`: the condense model at the condense ceiling as the assistant's key, `x-fors-step: escalation`,
  `max_tokens` 200, `Chat:EscalationTimeout` 2,000 ms, run at the same time as the follow-up call) with its input,
  language and accept rule; the second `kalla` chunk carries `follow_ups` and `escalation` (§3.8 Delivery);
  `Settings.Chat.Prompts.escalation` with its locked lines (`PromptTexts`, V38 ≤ 2,000, the prompt texts endpoint's
  section `escalation`, `labels.ts`'s section and member names); `rag.chat.escalation` and the `escalation` token
  step (§17); the FakeGateway's script per step already covers `escalation`.
- **Paths:** `src/Kalla.Answering/**`, `src/Kalla.Api/**`, `src/Kalla.AdminApi/PromptText*.cs`,
  `src/Kalla.Domain/**` (b's), `src/admin-ui/src/utils/labels.ts`, `tests/**` (not
  `tests/Kalla.ContractTests/**`, `tests/Kalla.Worker.Tests/**`).
- **Done when:** chat §18.9's *Escalation* tests pass, with a negative proof on the accept rule's masking; the
  Settings page's Prompt texts panel shows the escalation section with its locked lines (API test and Vitest).
- **Waits on:** nothing (B-47 first only to keep the two PRs apart on `ChatStreaming.cs`).
- **Read:** Decisions "Escalation hand-off", "Chat prompt texts configurable"; chat §3.8, §9.8, §9.9, §10.1, §17,
  §18.9; admin V15, V38; cost-attribution §4.1.

### [x] B-49 The unavailable text ends with the platform's links when the assistant has none (#860's deviation)
- **Do:** chat §10.2: the "search is temporarily unavailable" text ends with the contact links by §10.1's rule (the
  version's `NotFound.links`, else `Settings.Chat`'s), as the not-found and general-knowledge texts and as
  `kalla.escalation`'s `contacts` (§9.9) already do. Today `FixedTexts.Unavailable` gets only the assistant's links,
  so the text and `contacts` differ when only the platform has links.
- **Paths:** `src/Kalla.Answering/**`, `tests/**`.
- **Done when:** a test shows an unavailable turn of an assistant without links ending with the platform's links,
  and its `contacts` equal to the links in the text; one of an assistant with its own links shows those.
- **Waits on:** nothing.
- **Read:** chat §9.9, §10.1, §10.2; decided by the user, 2026-11-19 (decisions log).

### [x] B-50 Delete a knowledge base and a source in the admin UI; their names free at once (#878)
- **Do:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5 (the collections and sources rows)
  and *Delete in the UI* (§5.5): *Delete* in the ⋯ menu of a knowledge base and of a source (`Manage`), each with the
  dialog (the counts, the name typed; for a knowledge base in use the `collection_in_use` list and no button). The
  unique index on `config."Collections"."Slug"` becomes partial (`WHERE "DeletedAt" IS NULL`), and `slug_taken`
  looks only at knowledge bases that are not deleted. `collection_in_use` ignores deleted assistants once B-51 is in.
- **Paths:** `src/admin-ui/**` (b's), `src/Kalla.AdminApi/**`, `src/Kalla.Infrastructure/**` (the index and its
  migration), `tests/**`.
- **Done when:** tests show a knowledge base created with the name of a deleted one, the delete of one in use
  refused with its list, and both dialogs (Vitest); a browser check by hand of both deletes, named in the PR.
- **Waits on:** nothing. (That a deleted source's documents leave the index is A-36.)
- **Read:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5, §5.5; data-model §2.1, §8.

### [x] B-51 Delete an assistant (#878)
- **Do:** the same decision; admin §4.5 (the assistants row): `config."Assistants"."DeletedAt"` with the partial
  unique index on `Slug`; `DELETE /api/assistants/{id}` (`Manage`, from any state, the draft discarded, audit
  `assistant.delete`); a deleted assistant is in no list, answers 404 on every route, is served to no client
  (identity §4.3 step 5; `PublishedAssistantVersions` and the worker's column grant, data-model §2.2) and does not
  block a knowledge base's delete; *Delete* in the assistant's ⋯ menu with its dialog (the apps, the questions of
  the last 7 days, the name typed); statistics and feedback pages read *Deleted assistant* where the row is gone
  (`labels.ts`); `config.rag_retention_config_step` purges the row 30 days after `DeletedAt` once no conversation
  names it (data-model §8).
- **Paths:** `src/Kalla.AdminApi/**`, `src/Kalla.Api/**`, `src/Kalla.Answering/**`, `src/Kalla.Domain/**`
  (b's), `src/Kalla.Infrastructure/**`, `src/Kalla.MigrationService/**` (b's), `src/admin-ui/**` (b's),
  `tests/**`.
- **Done when:** tests show: a deleted assistant gone from `GET /v1/models` and refused in chat with 404 for a
  client that had it; a new assistant with its name; its knowledge base deletable; its conversations and daily
  totals still there; the retention step purging it only after the 30 days and its last conversation.
- **Waits on:** nothing (B-50 first only to keep the two migrations apart).
- **Read:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5, §5.5; identity §4.3;
  data-model §2.1, §2.2, §8.

### [x] B-52 Narrow a chat turn by topic: `kalla.topics` (F8)
- **Do:** chat §3.8: the request member `kalla.topics` (an array of 1–20 tag-format strings; 400 `invalid_request`
  with `param` `kalla.topics` otherwise; absent, `null` and `[]` change nothing; ignored for Open WebUI) and the
  answer's `kalla.topics` (the asked topics that a link searched); the narrowing of the published version's links
  (a link with topics: the common ones, out of the turn when none; a link without: the asked ones), passed as
  `RetrievalRequest.Topics` so the topic links, the coverage counts and the floor see the narrowed links
  (knowledge §4.2, §6.2); a turn with no link left ends `not-found` without a search.
- **Paths:** `src/Kalla.Api/**`, `src/Kalla.Answering/**`, `src/Kalla.Domain/**` (b's), `tests/**`; the
  OpenAPI document and `docs/` where the `kalla` members are listed.
- **Done when:** chat §18.9's *Topics per request* tests pass, with the negative proof that a request cannot
  reach a tag its link does not have; the Open WebUI recorded fixture is unchanged.
- **Waits on:** nothing.
- **Read:** Decisions "Topics per request"; chat §3.8, §18.9; knowledge §4.2, §6.2; processing §7 (the tag
  format); admin V11.

### [x] B-53 Purge the rows of deleted sources and knowledge bases (#878)
- **Do:** data-model §8: `config.rag_retention_config_step` deletes a `Sources` row with `DeletedAt` once no
  `content."Documents"` row of the source remains and none of its blob tombstones waits for its purge, and a
  `Collections` row with `DeletedAt` once its sources are gone. Deletion proposals, frontier and run rows of the
  source go by their own retention, as for a live source (settled on #884).
- **Paths:** `src/Kalla.AdminApi/**`, `src/Kalla.Infrastructure/**`, `tests/**`.
- **Done when:** a test shows a deleted source's row kept while a document or a tombstone of it is left and
  purged after, and its knowledge base's row after its last source.
- **Waits on:** nothing.
- **Read:** data-model §8; Decisions "Deleting knowledge bases, sources and assistants"; #884.

## Main session (`main.md`)

### [x] M-22 Deploy simplification (user, 2026-11-16; closed 2026-11-20, S8 as a standing rule)
- **Do:** [`../proposals/deploy-simplification.md`](../../proposals/deploy-simplification.md), starting with S1 (one
  bootstrap for Compose and Portainer; decided 2026-11-16, Decisions "One deployment bootstrap"; the proposal's S1
  lists its five steps, one pull request each), S3 with S9 (Open WebUI as a profile in Compose, off unless asked for
  and invisible when off) and S4a (delete the upgrade paths older than the VM's version). Upgrades may break, but each
  breaking change gets an upgrade path where one is feasible (user, 2026-11-16). The setup wizard works the same in
  every form. M-17's C13 is absorbed by S1's step 3. S2, S5 and S7 were left to the main session (user, 2026-11-16).
- **Paths:** `deploy/**`, `docs/**`, `.github/workflows/**` (stream a's: `main-session/<slug>` branches, no open `a/`
  work at the start); the admin UI's texts for S9 are stream b's (announce on an issue first).
- **Status (2026-11-16, session 46):** S1's four points decided. Merged: #798 (S1 steps 1 and 4: `bootstrap.sh` takes
  its paths and inputs as variables, `create-secrets.sh` runs it; an existing Kubernetes installation keeps its
  secrets), #799 (S4a: the upgrade sections older than 0.5.x, with a pointer to the `v0.7.0` guide) and #800 (S1 steps
  2 and 3 with S3: `deploy/compose.rag.yaml` and its overlays, Compose on `kalla-bootstrap` and volumes, Open WebUI as a
  profile, the 193-line `init-deployment.sh`; the Portainer stack renders as before; booted in dry run 43941252174).
  Also merged: #806 (S9, the deploy part: Open WebUI out of the example's default profiles, nothing of it generated
  without the profile); the design follows the new files (`repo-and-deployment.md` §1, §3, §5 to §10, Decisions
  "Pilot containers" and "Deployment values across releases"); #808 (step 5: the operator guides; `--gateway-ca-file`
  copies the gateway's CA certificate instead of mounting its directory; the bootstrap's checklist speaks for its
  form). **S1 is done.** #809 (S9's guides: the Portainer first deploy finishes without Open WebUI, *Adding Open
  WebUI*). #810 (S2: the quick start installs without host hardening, which is its step 7). Session 47: S4b and S4c in one pull
  request (`kalla-migrate` fences the service roles a migration stops, data-model §10; an upgrade from 0.5.x is one
  step, boot-tested from 0.5.1 in `release.yml`; `docs/portainer.md` has one *Upgrade* section; Decisions "Upgrades in
  one step"), and S9's admin UI part (#816, closes #807). S6 (one escrow set: `escrow-keys.sh` with the pepper, the blob keys, the passphrase, the database passwords, the conversation id keys, the Data Protection certificate and the CA, by `--env-file`, `--project` or directories, with `--restore`; repo-and-deployment §7.4). Open: S8 as each page is touched. S5 and S7 are proposals in the gateway's
  repository (thekim1/fors#108, #109).
- **Done when:** a first install with Compose needs the same few values as the Portainer stack and no bootstrap logic
  exists twice; an installation without Open WebUI shows no trace of it; each step shown by the deploy tests
  (`deploy-checks`, the boot test) and, for a stack-file change, a boot.
