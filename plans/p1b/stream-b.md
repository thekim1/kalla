# Stream B brief: the serving side (P1b)

You harden the serving side for the pilot: the `chat` schema, age decay and `demote` in retrieval, the admin API and
the journey pages for sources, publishing and the assistant (drafts, versions, restore, test panel), the
generic forms, history mode `none` with feedback (writer, import, Feedback page), accessibility and the pilot journey
E2E, the W3 spike and the switch to gateway release 1. Stream A builds the worker side. You start P1b once your P1a
**code** items are done or blocked on a live hand-off (Decisions table, "Code first, live testing last"); the live
items wait for the end block (`README.md` §5 step 4).

## Read first

1. [`../../AGENTS.md`](../../AGENTS.md): working rules.
2. [`README.md`](README.md): what P1b is (§1, code vs live), ownership, the checklist (§3, binding done-when),
   hand-offs, sync points, open questions (§7).
3. [`../weakness-review.md`](../weakness-review.md) §1–§5 and [`../simplification-pass.md`](../simplification-pass.md)
   §5, §8, §10, §15.
4. [`../admin/plan.md`](../admin/plan.md) §5, §8 and [`../admin/design.md`](../admin/design.md) §4, §5, §6.4–§6.7, §7,
   §8, §10, §11, §12 (P1b rows).
5. [`../chat-api/design.md`](../chat-api/design.md) §11–§14, §18; [`../knowledge-api/design.md`](../knowledge-api/design.md)
   §6.5, §16.1; [`../platform/data-model.md`](../platform/data-model.md) §2.3, §8; [`../gateway-changes/design.md`](../gateway-changes/design.md)
   §13.4. Each design ends with "Deferred or removed": nothing listed there is P1b.

## Items, in order

| Order | §3 # | Kind | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|---|
| 1 | 1 | code | **First, because stream A waits on it (L2).** Schema part 3: `chat` tables, freshness columns not in P1a's item 2 | data-model §2.3; weakness review §3 | – |
| 2 | 3 | code | Age decay and `demote` in `Ume.Rag.Retrieval`; `website` template default in the write path → **L3** | knowledge §6.5, §16.1; weakness review §3 | – |
| 3 | 9 | code | Admin API for P1b → **L4** (validate against L1 when it lands) | admin §4, §6.6, §6.7, §12; identity §4.9 | 23 |
| 4 | 10 | code | Chat: conversation ids, writer in mode `none`, retention, user delete, `/v1/feedback`, `FeedbackImporter` and pruning → **L6** | chat §11–§14 | 20, 51, 135 |
| 5 | 11 | code | Journey pages part 1: Kunskapsbanker, Källa, Publicering (real results after **L5**) | admin §5.3–§5.5, §6.4–§6.7 | – |
| 6 | 12 | code | Journey pages part 2: evaluation, Sökinställningar, Assistenter with versions and Testa, Översikt checklist, generic forms, Granskningslogg, explain access (Delning comes with item 36) | admin §5.3, §5.8, §7 | 146 |
| 7 | 13 | code | Feedback page | admin §8.1; chat §14.4 | – |
| 8 | 14 | code | Accessibility pass, tillgänglighetsredogörelse, pilot journey E2E → **L7** | admin §10.1, §11.4 | 96 |
| 9 | 18 | code | W3 spike on the dev Keycloak and a local Open WebUI | weakness review §5; postponed W3 | 174 |
| 10 | 33 | code | Organisation schema → **L13** | data-model §2.1; Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)" | – |
| 11 | 34 | code | Topics in retrieval → **L14** | knowledge §4.2, §6.2 | – |
| 12 | 35 | code | Organisation in the admin API: sharing, `view=`, topics, Map route, contact, V11, V35 | admin §3.2, §4.5, §4.6 | – |
| 13 | 36 | code | Organisation pages: menu, Sharing and Topics tabs, knowledge picker with topics, "Who can use it", Map table, no team field (after #314) | admin §5.2–§5.5 | – |
| 13a | 39 | code | A new assistant starts from a default: `Settings.Chat.DefaultSystemPrompt` in the first draft; model and query settings under "Advanced" | admin §5.5.4; chat §5 | – |
| 13b | 40 | code | Grounding mode per assistant: frames per mode, general-knowledge answers behind the caveat line, `answered-general`, V17, V36, "Answers from" and the badge (after 36 and 39) | chat §6.2, §7.3, §10.4; admin §4.6, §5.5.4; data-model §2.1, §2.3 | – |
| 14 | 19 | code | Code complete; your part of the go-live pin review (NuGet, npm; the `sv_SE` list; the realm callback for the Open WebUI pin, README §7 O8) | Decisions "7-day rule in development" | 113 |
| 15 | 20 | live | P1a's live items, with stream A and the user | `../p1/README.md` §3 | as listed there |
| 16 | 25 | live | Decay and `demote` on the gold set's temporal questions; HNSW recall vs exact on the pilot collection | weakness review §3, "Lower-ranked findings"; knowledge §6.4 | 117, 171 |
| 17 | 27 | live | Gateway release 1 switch (**after L9**) | gateway-changes §13.2–§13.4 | 41 (release-1 part) |
| 18 | 28 | live | Feedback import live, with stream A | chat §14.2 | 49, 135 |
| 19 | 30 | live | E1 spike, or moved to P2 (README §7 O3) | identity §9.4, §16 | 175 |
| 20 | 31, 32 | live | Go-live gate and pilot live, with stream A and the user | README §1.1 | – |

If an item is blocked on a hand-off, start the next one and come back later. Answer stream A's table or column
requests (issues labelled `stream-b`) with a migration, ahead of your other work.

**Not in P1b** (P2 or later; weakness review §1, §4; admin plan §8; chat plan §10): assistant version diff,
per-assistant test questions and the answer judge, assistant templates and copy, the Map drawing and the
unused-assistant attention item, site-profile proposals, effect-tag publish plans, the coverage histogram, parameter
diff, audit diff viewer, Historik tabs; history modes `stored`/`session`, chat bucket keys, content encryption,
crypto-shredding, comment reveal, conversation review and the access log (all wait for A1); skill share lists; `/mcp`; department scope from gateway member groups (release 2).

## You provide, you consume

- **Provide:** L2 (schema; announce it in an issue labelled `stream-a`), L3 (decay and `demote`), L4 (admin
  endpoints), L6 (chat writer, feedback endpoint and importer with their configuration names), L7 (E2E specs and
  fixture site).
- **Consume:** L1 (contracts: validate the new source fields and job payloads against them; until then the admin §4.7
  field lists), L5 (worker results; until then recorded `Jobs.Result` and `EvaluationRuns` fixtures), L9 from the
  gateway session, L12 from the user; J10, J11 and J13 from P1a.
- **AppHost, TestKit, `tests/e2e` and `src/admin-ui`** are yours. Your new path is `docs/accessibility-statement.md`
  (README §2). A asks for changes through issues.

## Finished when

Every row above meets its §3 done-when with evidence in its PR, and every listed `to-verify.md` row is recorded in
`results-b.md`. A department admin can run the whole journey (admin §5.4) through the journey pages, an assistant
draft can be tested, published and restored, pilot ratings reach the Feedback page, every call runs against gateway
release 1, and the graded gold-set answers met the go-live gate.
