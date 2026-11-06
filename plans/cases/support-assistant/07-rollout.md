# Rollout

The order of work from decisions to go-live, then what comes later. Steps name their owner and point to the file
with the detail. The quality gate is the platform's own: a hand-graded gold set (Decisions "Weakness review 2").

## 0. Decisions and inputs (before any setup)

| # | Input | Who |
|---|---|---|
| 0.1 | Answers to the open questions: **done** ([`README.md`](README.md), "Decisions (user, 2026-11-06)") | user |
| 0.2 | DPIA (G1) covers pasted tickets sent to an EU-hosted model and the app's own chat store | user with the data protection officer |
| 0.3 | Gaps built before the pilot: **done**. SA-1 and SA-2 (#433), SA-3 (#434), SA-4 (#431), SA-6 (#430) and SA-7 (#432) are on `main` ([`06-gaps.md`](06-gaps.md)) | main session |
| 0.4 | Named people: knowledge-base editor (contact), department admin, the frontend team's contact, 3–5 reviewers from support | user, support organisation |
| 0.5 | An inventory of the sources: intranet sections, file folders, Markdown repositories, file types (legacy Office?) | support organisation |
| 0.6 | Confirm the condense model: `claude-haiku-5-5` recommended for the long-ticket condense and the follow-ups ([`01-gateway-setup.md`](01-gateway-setup.md) §1) | user |

## 1. Platform readiness

| # | Step | Detail |
|---|---|---|
| 1.1 | Ume.Rag deployed against the gateway (test host first) | [`../../../docs/gateway-host-setup.md`](../../../docs/gateway-host-setup.md), [`../../../docs/production-setup.md`](../../../docs/production-setup.md) |
| 1.2 | Gateway models (optional reranker: the Foundry workaround until llmgateway#72), keys, PII policy | [`01-gateway-setup.md`](01-gateway-setup.md) |
| 1.3 | `--crawl-allow` for the intranet ranges; robots.txt allowance if needed | [`02-knowledge-base.md`](02-knowledge-base.md) §2 |
| 1.4 | Department and admins in Keycloak realm `ume`; organisation sync | [`02-knowledge-base.md`](02-knowledge-base.md) §5 |
| 1.5 | `Settings.Chat.allowedLinkHosts` includes the intranet host | [`04-assistant.md`](04-assistant.md) §6 |
| 1.6 | `Settings.Chat.condenseModel` set (0.6); `Chat:LongQuestionCondenseTimeout` raised to 4 s only if `gpt-6-luna` stays | [`04-assistant.md`](04-assistant.md) §2 |
| 1.7 | `Settings.TrustedProxies` = the reverse proxy's addresses, so key allow-lists and the download limit see the client | [`05-frontend-integration.md`](05-frontend-integration.md) §2 |

## 2. Knowledge base

| # | Step | Detail |
|---|---|---|
| 2.1 | Collection, crawl source, preview, first sync | [`02-knowledge-base.md`](02-knowledge-base.md) §2, §5 |
| 2.2 | Upload source with the Word, PDF and Markdown files (or the publish-and-crawl route for Markdown) | [`02-knowledge-base.md`](02-knowledge-base.md) §3–§4 |
| 2.3 | Check the run reports, failures by reason, personal-data report, extracted text of a sample | admin §5.4 steps 5–6 |
| 2.4 | Daily schedule; noise selectors so unchanged pages stay unchanged | [`03-updates.md`](03-updates.md) §2 |
| 2.5 | Generated evaluation set and first retrieval report | processing §13, §14 |
| 2.6 | Publish the knowledge base | knowledge §4.5 |

## 3. Assistant

| # | Step | Detail |
|---|---|---|
| 3.1 | Draft with the ticket-solving instructions, `generalKnowledge` `fallback`, not-found texts with escalation links | [`04-assistant.md`](04-assistant.md) |
| 3.2 | Try twenty real (anonymised) tickets in the test panel; adjust instructions | admin §4.9 |
| 3.3 | Publish | admin §5.4 step 11 |

## 4. Gold set and evaluation

The gold set is written from **real tickets, rewritten without personal data** (the gold set is stored in the
platform; Decisions "Gold set drafted by the main session, reviewed by DoIT" shows the process used for umea.se):

| Part | Size (Decisions "Weakness review 2") | For this case |
|---|---|---|
| Answerable | 100–150 | common ticket types with the article that solves them |
| Unanswerable and near-miss | 30–50 | tickets the knowledge base does not cover, and look-alikes (wrong system, wrong version) |
| Temporal | ≥ 10 | questions whose answer changed (a new VPN client, a moved service desk) |

| # | Step | Detail |
|---|---|---|
| 4.1 | Enter the questions with their expected documents as the `human` layer of the collection's evaluation set | `POST /api/collections/{id}/evaluation/questions` (`src/Ume.Rag.AdminApi/EvaluationEndpoints.cs:71`); admin §7.1; processing §13.4 |
| 4.2 | Run retrieval evaluation; compare hybrid with rerank (`hybrid-rerank-gateway` with the Foundry `Cohere-rerank-v5-fast` alias, `hybrid-rerank` with the chat-model reranker) if a rerank model is set | processing §14; `scripts/evaluation/rerank.sh` ([`../../../scripts/evaluation/README.md`](../../../scripts/evaluation/README.md)) |
| 4.3 | Set the not-found thresholds from answerable and unanswerable questions | Decisions "Weakness review 2"; knowledge §7 |
| 4.4 | Generate answers for the gold set and have the support reviewers grade them | the go-live gate below |
| 4.5 | Confirm `generalKnowledge` (`fallback`, or `supplement` if general answers mislead) and decide rerank (on only with a gold-set gain) from 4.2–4.4 | [`04-assistant.md`](04-assistant.md) §4–§5 |

Per-assistant test-question sets and automatic answer evaluation are P2 (admin §7.4; chat §18.8); in P1 the hand
grading is the gate.

## 5. Frontend integration

| # | Step | Who | Detail |
|---|---|---|---|
| 5.1 | The frontend team builds against a local Ume.Rag with the FakeGateway | frontend team | [`05-frontend-integration.md`](05-frontend-integration.md) §8 |
| 5.2 | Client, key (with `requestsPerMinute` and `allowedIpRanges`), audience, proxy routes, `Knowledge:PublicBaseUrl` on the test host | Ume.Rag admins, operator | [`05-frontend-integration.md`](05-frontend-integration.md) §2, §9 |
| 5.3 | Contract check: a cited answer with related documents and follow-ups (stream and JSON), a not-found, an uploaded-file download, a crawled-page link, a feedback rating, a long ticket, a call from outside the allow-list | both teams | [`05-frontend-integration.md`](05-frontend-integration.md) §9 step 7 |

## 6. Pilot

- **Who:** 5–10 support staff, 2–4 weeks, on real tickets, alongside their normal routine.
- **What they do:** paste the ticket, use the answer and its sources, rate every answer (thumbs up or down with a
  reason; [`05-frontend-integration.md`](05-frontend-integration.md) §3.6).
- **What the editors do weekly:** read the feedback page (admin §8.1) and the not-found and general-answer counts
  (`AssistantDailyTotals`; `rag.chat.requests{outcome}`), write or fix the articles the misses point to, add the
  misses as gold-set questions.
- **What the operators watch:** run failures and deletion proposals waiting for a decision (connectors §13.5),
  gateway budget and errors ([`../../../docs/runbook.md`](../../../docs/runbook.md) "Daily checks").

## 7. Go-live checklist

- [ ] Go-live gate met on the support gold set, hand-graded: correct ≥ 80 %, citation precision ≥ 90 %, false
      not-found ≤ 10 %, off-topic not-found ≥ 80 % (Decisions "Weakness review 2").
- [ ] DPIA (G1) signed for pasted tickets and the app's store; the app shows the same warning against confidential
      input that Open WebUI shows (Decisions "Weakness review 4": banner).
- [ ] The app key's `RequestsPerMinute` set and recorded; its `AllowedIpRanges` and `Settings.TrustedProxies` set, and
      a call from another address refused with 403 `address_not_allowed`.
- [ ] Key expiry and rotation owner named; key stored only in the app's secret store.
- [ ] Daily crawl runs complete; no deletion proposal left waiting.
- [ ] Escalation links in the not-found text work from the app.
- [ ] Runbook entry for the app: who to call when rag-api answers 5xx, and how to revoke the key.
- [ ] The platform's own go-live items (backups, restore drill, R1) done for the deployment ([`../../roadmap.md`](../../roadmap.md)).

## 8. Later

| Step | What it needs | Reference |
|---|---|---|
| **Ticket-system integration, read**: the app or the assistant fetches a ticket by number instead of a paste | either the app does it (no Ume.Rag change: it reads the ticket and sends the text), or a read-only tool on the P2 tool hub that the assistant calls | tools design; roadmap "P2: Tools and skills" |
| **Ticket-system integration, write back**: a drafted reply or a resolution note into the ticket | write tools with confirmation are P3; until then the app writes back what the user approved, outside Ume.Rag | roadmap "Later: P3" |
| Similar solved tickets as a source | a collection of anonymised, resolved tickets (export to files, or a connector); personal data in tickets needs its own DPIA decision | processing §9 |
| Per-user identity, department audiences | SA-5 | identity §7 |
| Intranet in SharePoint | the P2 SharePoint connector | connectors §14 |
| On-prem models; `sensitive` support content | on-prem hardware (M2) | Decisions "M2 and test hosting" |
| Stored conversations for review | A1 (records management) | Decisions "Weakness review 4"; [`../../postponed-decisions.md`](../../postponed-decisions.md) |
