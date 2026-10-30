# Stream B brief: the serving side (P1a)

You build the serving side of the walking skeleton: the schema, retrieval and the knowledge API, the chat API, the
admin API the platform team sets the pilot up with (Scalar), the gateway client side of release 1 and RAG's realm
additions for the shared Keycloak. Stream A builds the worker side. You start at J1 (`README.md` §4).

## Read first

1. [`../../AGENTS.md`](../../AGENTS.md): working rules.
2. [`README.md`](README.md): what changed since the design (§1, EU-hosted models), ownership, the checklist (§3, binding
   done-when), hand-offs, sync points, rules, open items (§8).
3. [`../weakness-review.md`](../weakness-review.md) §1, §2, §4, §5 and [`../simplification-pass.md`](../simplification-pass.md)
   §1, §5–§9, §14, §15.
4. [`../platform/data-model.md`](../platform/data-model.md): all of it. You own every migration.
5. Per item, the references below: [`../knowledge-api/design.md`](../knowledge-api/design.md),
   [`../chat-api/design.md`](../chat-api/design.md), [`../admin/design.md`](../admin/design.md) §4 and §12,
   [`../identity/design.md`](../identity/design.md) §4–§6, [`../gateway-changes/design.md`](../gateway-changes/design.md) §13.
   Each ends with "Deferred or removed": nothing listed there is P1a.

## Items, in order

| Order | §3 # | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|
| 1 | 2 | **First, because stream A waits on it (J2).** Schema part 1, ingestion (incl. `BlobTombstones` #34 and the profile's ceiling, README §8 O1) | data-model §1.2, §2.1, §2.2, §3.2, §9, §10; connectors §6.0 | 22 |
| 2 | 3 | Schema part 2, serving (assistant versions and the published view, `GatewayModels`, evaluation tables with `Variant` and coverage fields) | data-model §2.1, §2.2; weakness review §2 | – |
| 3 | 11 | Retrieval core in `Ume.Rag.Retrieval` → **J5** | knowledge §6–§8, §16.1–§16.2 | 4, 5, 6, 7, 19 |
| 4 | 13 | Gateway client, release-1 side: per-level keys, `GatewayModels` sync, V8 check, pilot embedding profile and canary, gateway status | gateway-changes §13.4; admin §4.10; identity §4.7 | – |
| 5 | 12 | Knowledge role: `KnowledgeService`, query embedding at the profile's ceiling, sections, citations, download grants, `/v1/retrieve` | knowledge §2–§5, §9, §10, §16.3, §16.6 | 95, 102, 162 |
| 6 | 14 | Admin API set-up surface in Scalar (README §3 item 14), with `AdminClientAddresses` once `Settings` exists → **J7** | admin §4.1–§4.7, §12 | 32 |
| 7 | 15 | Chat API and Open WebUI user-JWT trust → **J9** | chat §3–§10, §15.1, §18; identity §5 | 42, 47, 48, 50, 103, 105, 136, 154, 164, 165 |
| 8 | 16 | Realm-additions file in `deploy/keycloak/` → **J8** | identity §11; Decisions table "P1 pilot sign-in"; #47 | 83 |
| 9 | 17 | Evaluation smoke set `tests/evaluation/smoke` and its test (A adds the CI job) | knowledge §16.4 | – |
| 10 | 21 | Gold set entered through Scalar (**after J13**); thresholds saved from the three-way run | weakness review §2; knowledge §7 | 143, 169 |
| 11 | 22 | P1a done, with stream A and the user | README §3 | – |

If an item is blocked on a hand-off, start the next one and come back later. Answer stream A's table or column
requests (issues labelled `stream-b`) with a migration, ahead of your other work.

**Not in P1a** (P1b or later; README §6): journey pages and generic forms (README §8 O2), preview and publish of
pipelines, assistant versions list and restore, the test panel, feedback and its import, the conversation writer and
history metadata, age decay and `demote`, `/mcp`, chat bucket keys and content encryption (history mode `none`).

## You provide, you consume

- **Provide:** J2 (schema; announce it in an issue labelled `stream-a`), J5 (`RetrievalEngine`), J7 (Scalar set-up),
  J8 (realm additions, through the user to the gateway session), J9 (chat API, Open WebUI client and key).
- **Consume:** J3 (contracts: validate source settings against them; until then the admin §4.5 field lists), J4
  (real chunks; until then a TestKit fixture writer), J6 (commands applied), J10, J11 and J13 from the user and the
  gateway session.
- **AppHost and TestKit** are yours, incl. the `open-webui` resource for the local skeleton (sync point 3). A asks for
  changes through issues.

## Finished when

Every row above meets its §3 done-when with evidence in its PR, and every listed `to-verify.md` row is recorded in
`results-b.md`. The platform team set the pilot up through Scalar alone, the thresholds are calibrated from the gold
set, and a pilot member's cited answer has been shown in Open WebUI.
