# Personal-data review (2026-11-21)

The question (user, 2026-11-21): **can an editor review personal-data findings and mark them as OK, as a false
positive, or reject the document?** This is the main session's answer, checked against the designs (processing §9,
admin §4.5 and V26, engine §9.1) and the code on `main` after 0.9.1. It is a proposal, not a binding design: what it
proposes becomes binding only through a Decisions entry and the designs for its areas.

## What there is today

| Part | Designed | Built |
|---|---|---|
| Detection: patterns on every set, NER only when names can change the flag; counts and fired types on the set, never values | processing §9.1–9.4 | yes |
| Hold: a flagged set in a `public` collection is `held`; with `piiMode: report` only personnummer and samordningsnummer hold | processing §9.5 | yes |
| One decision per document, **clear** (`clear-pii-flag`, reason ≥ 10, Manage, audit 6001 `pii.clear`), which releases the held set and covers later sets while no count rises | processing §9.5, admin §4.5, V26 | API and worker yes; **no button in the admin UI** |
| Highlights for the person deciding: `POST /api/documents/{id}/pii-preview` (spans from 24 h preview blobs, audited `pii.view`) | admin §4.5 | API yes; not in the UI |
| The web editors' report per source (`GET /api/sources/{id}/pii-report`) | processing §9.5 | yes, read-only, on the source page |
| Flagged and cleared badges and the `pii` filter on the document list | admin §4.5 | yes |
| Masking (`pii-mask`) | processing §9.7 | P2, not built |

So a held document can be released today only through the API, and there is a single decision, *clear*, with no record
of **why**. False positives are meant to be handled by the rules (§9.2's look-alikes, `Settings.PiiFlagRules`), but
nothing feeds the people's findings back to whoever tunes them.

## What is proposed

### 1. Three decisions instead of one

The decision is still on the **document**, by the same people (`rag-department-admin` of the source's department, or
`rag-platform-admin`), with a reason of at least 10 characters (V26). The detected values are still never stored
(Decisions "Personal data"), so a decision covers the document's fired **types**, not single findings.

| Decision | Meaning | Effect |
|---|---|---|
| **Approve** | the personal data is real and may be published (a named official's contact page, say) | today's `clear-pii-flag`: releases the held set; later sets are covered while no count rises |
| **False positive** | what was found is not personal data; the editor ticks which fired types were wrong | as *approve*, plus the verdict and the ticked types are stored, so the rules can be tuned from them |
| **Reject** | the document must not be served | the document is kept out: its held set stays held, a live flagged set (`piiMode: report`) is withdrawn, and every later set is held until the decision is undone |

*Reject* is fail-closed: content that changes does not lift it. To undo it, someone with Manage clears the document
(*approve* or *false positive*), with a reason. For a crawled page the dialog also offers to add an `exclude` pattern for
the page's URL to the source (connectors §5.2), so the page is no longer even fetched. For an uploaded file it points to
removing the file from the source.

### 2. Data

- `Documents`: `PiiDecision` (`approved` | `false_positive` | `rejected`, NULL before any decision) and
  `PiiFalsePositiveTypes text[]` next to the existing `PiiClearedAt`, `PiiClearedBy` and `PiiClearedCounts`. A reject
  sets `PiiDecision` and the `…At`/`…By` columns, with no counts.
- Commands: `clear-pii-flag` takes a `verdict` (`approved` | `false_positive`) and the types; a new `reject-pii-document`
  (dedup `pii-reject:{documentId}`). Audit 6001 `pii.clear` with the verdict, and a new `pii.reject`. The reason stays
  in the audit event, as today.
- The swap rule (engine §14 rule 3) gains one case: a document with `PiiDecision = rejected` swaps every new set in
  `held`, whatever the counts and the source's `piiMode`.

### 3. Admin UI

- **The document page's personal-data panel:** the counts per type and the fired types; *Show highlights*
  (`pii-preview`, audited); the three decisions, each with its reason dialog and the consequence spelled out, as the admin
  design's other destructive actions do ("The page stays out of the knowledge base until someone clears it").
- **The source page's report:** a *Decision* column (none, approved, false positive, rejected, with who and when) and a
  filter *Not reviewed*, so the web editors can work through the list.
- **Home's *Needs attention*** (B-59) counts held documents with no decision.
- **A false-positive summary** for platform admins: per type, how many documents were marked false positive in the last
  90 days, with links. It is a report for whoever tunes `Settings.PiiFlagRules` and §9.2's keywords. Nothing changes the
  rules automatically.

## Order

1. **The *Clear* button and the highlights** on the document page. Already designed (admin §4.5) and built in the API:
   a stream item with no new decision.
2. **Verdicts** (*approve* and *false positive*) with the report's *Decision* column and *Not reviewed* filter.
3. **Reject**, with the swap rule's new case and the exclude-pattern shortcut.
4. **The false-positive summary**, once there are verdicts to count.

Steps 2–4 need a Decisions entry and changes to processing §9.5, engine §14, admin §4.5 and V26, and data-model
`Documents`.

## Decided (user, 2026-11-21)

Asked with four alternatives each on #925; recorded in `archive/decisions-log.md` (2026-11-21, session 53):

1. **Reject is a state of its own** on the document, fail-closed, the same for crawled and uploaded documents. The
   exclude-pattern shortcut stays as an offer in the dialog.
2. **A false positive needs the ticked types**, not only the reason.
3. **Who decides stays as today:** the admin of the source's department, or a platform admin, with a reason of at least
   10 characters, audited.
4. **Step 1 is queued now** in P2 wave 1 as stream b's **B-63** (`work/stream-b.md`): the *Clear* button and the
   highlights. It needs no new decision (admin §4.5).

Steps 2–4 get their Decisions entry and design changes from this proposal's session, with the review's two points
(main session on #925):

- *Reject* withdrawing a **live** set under `piiMode: report` goes further than *clear* does today: processing §9.5 and
  the swap rule in engine §14 both have to say it.
- The *Not reviewed* filter and the *Needs attention* count (B-59) both need "held with no decision". If B-59 lands
  first, it counts held documents now and adds "no decision" with step 2.
