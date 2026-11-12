# Gateway setup

What the LLM gateway operator provides for this case. The deployed gateway is v0.9.1 (residency ceiling, optional
rerank, rerankers on Azure AI Foundry, shared Keycloak; verified live, B-L4, #441; Decisions "Gateway release 1 and
shared Keycloak as built"); v0.9.2 (2026-11-07) adds Keycloak groups shared with Ume.Rag and changes nothing for the case. Ume.Rag needs no gateway change for the
case: every need below is an existing gateway feature.

If Ume.Rag is already deployed against the gateway for the pilot ([`../../../docs/gateway-host-setup.md`](../../../docs/gateway-host-setup.md)),
§1–§3 are checks, not new work: the support assistant reuses the deployment's keys and embedding profile.

## 1. Models

| Use | What | Residency | Binding rule |
|---|---|---|---|
| Answer | a chat model the gateway lists within `Eu` | `Eu` | `internal` = on-prem or EU (Decisions "Residency per level"); no on-prem models yet, so everything runs EU-hosted (Decisions "M2 and test hosting") |
| Condense (rewrites follow-up turns and long tickets into a search query; writes the follow-up suggestions) | a small chat model in `Settings.Chat.condenseModel`; **`gpt-6-luna`** (Decisions "Condense on the EU route"; user, 2026-11-06), with the budgets below | `Eu` | Decisions "Condense on the EU route"; chat §5, §5.1, §9.8 |
| Embedding | `text-embedding-3-large` at 1024 dimensions, the platform's pilot embedding profile | `Eu` | Decisions "Pilot embedding model, U-b". The knowledge base uses the platform profile; a different model would re-embed everything |
| Evaluation (question generation, judge) | any chat model in `Settings.EvaluationModel` / `EvaluationJudgeModel` | the collection's ceiling, `Eu` today | Decisions "Evaluation models at the collection's ceiling" |
| Rerank (optional) | `Cohere-rerank-v5-fast` on Azure AI Foundry (EU), chosen by the user instead of Berget AI `bge-reranker-v2-m3` (2026-11-06; Ume.Rag #421); or a small chat model for the interim chat-model reranker | `Eu` | Decisions "Rerank pulled forward", "Gateway release 1 …": optional everywhere, off by default |

**The condense model.** It makes up to two calls per turn for this case: the long-ticket condense (budget 3 s) and
the follow-up suggestions (budget 2 s). Measured on the interim gateway (chat §22, 2026-11-06, three runs each):

| Call | `gpt-6-luna` | `claude-haiku-5-5` |
|---|---|---|
| Long condense of a 16,000-character ticket | 2.3–2.9 s (tight against 3 s) | about 1.1 s |
| Follow-ups (N = 3) | 1.4–2.6 s (one run over 2 s) | 1.1–1.3 s |

**The condense model stays `gpt-6-luna`** (user, 2026-11-06), as Decisions "Condense on the EU route" sets: both
models wrote equally good queries and suggestions, and `gpt-6-luna` is the decided EU condense model. Its slower
calls are covered by the budgets: set `Chat:LongQuestionCondenseTimeout` to 4 s and `Chat:FollowUpTimeout` to 3 s
for this case ([`04-assistant.md`](04-assistant.md) §2). A long ticket then waits up to about 3 s before the first
token; the follow-ups only delay the end of the stream. `claude-haiku-5-5` stays the faster alternative if the pilot
shows the wait matters.

**On the local stack** (the proof of concept, [`07-rollout.md`](07-rollout.md) §0b) the model mirror is filled by
hand (model sync is off locally) and holds `claude-haiku-5-5`, `claude-opus-5-5` and `Cohere-rerank-v5-fast` today. Add
a manual mirror row for `gpt-6-luna` to try the decided condense model, or run the proof of concept on
`claude-haiku-5-5` (the measured faster alternative above) with the default budgets.

Gateway side: providers and models are added as in `~/git/llmgateway/docs/providers-and-models.md` (rerank:
section "Reranking (optional)"). Ume.Rag side, after any model change: a platform admin runs the model sync
(`POST /api/gateway/sync`, `src/Ume.Rag.AdminApi/GatewayEndpoints.cs:144`) and, for an embedding change, the embedding
canary (`POST /api/gateway/canary-check`; Decisions "Embedding canary on demand"). Rerank models are set in
`PUT /api/settings/retrieval` ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Rerank
(optional)").

**The Foundry reranker.** Gateway v0.9.1 routes rerankers on Azure AI Foundry natively (thekim1/llmgateway#72, ADR
019; release notes "Rerankers on Azure AI Foundry, and a residency per model"); the earlier OpenAI-compatible
workaround is no longer needed:

1. Gateway admin UI: the model `Cohere-rerank-v5-fast` under the existing Foundry provider, of kind *Rerank*, with its
   residency `Eu` (the residency is per model since v0.9.1).
2. Ume.Rag: model sync, then `Settings.Retrieval.gatewayRerankModel` = that model's alias (`PUT /api/settings/retrieval`).

It was measured live on `turkos` (B-16, #421, #438); `Retrieval:GatewayRerankTimeout` defaults to 1.5 s for it
(Decisions "Gateway rerank time limit 1.5 s").

Rerank stays optional and off by default: an assistant uses it (`retrieval.rerank` `gateway`) only if the gold set
shows a gain ([`04-assistant.md`](04-assistant.md) §5). The chat-model reranker (`Settings.Retrieval.rerankModel`,
`retrieval.rerank` `chat`) remains the interim option.

## 2. Keys

| Key | Holder | Settings | Reference |
|---|---|---|---|
| One gateway key per RAG deployable: `api`, `admin`, `worker` | `init-deployment.sh --gateway-key-file api:Eu=…` etc. | allowed residency `Eu` only; the worker never gets an `External` key | [`../../../docs/gateway-host-setup.md`](../../../docs/gateway-host-setup.md) "3. Inputs you need"; [`../../../docs/production-setup.md`](../../../docs/production-setup.md) "2. `init-deployment.sh`" |
| Gateway Data API key, `catalog.read` (until B-35/A-30; then `rag-admin`'s credential key with the scope `organisation.read`) | the admin's organisation and model sync | – | same |

The support assistant's chat traffic runs on the **api** key, shared with every other assistant on the deployment.
Gateway budgets and usage therefore show it under the RAG team's key, not under the support department (see
question 1 in [`README.md`](README.md)). The frontend team gets **no** gateway key: it calls Ume.Rag, never the
gateway ("Every model call goes through the gateway", `AGENTS.md`).

## 3. Residency ceiling

Ume.Rag sends `x-ume-max-residency` on every model call, derived from the level of the data in the call (identity
§4.7), capped by the deployment knob `Gateway:MaxResidency` (`src/Ume.Rag.Answering/Chat/ChatService.cs:65`). An
`internal` assistant therefore calls at most `Eu`. The gateway refuses with `403 max_residency_not_met` when no
target within the ceiling exists, and answers `503 no_eligible_provider` when in-ceiling targets are drained; Ume.Rag
retries only with the same ceiling (Decisions "Gateway release 1 …"; gateway ADR 018 in
`~/git/llmgateway/docs/adr.md`).

Optional hardening: *Require residency header* (`requireMaxResidency`) on the three RAG keys, so a call without a
ceiling fails instead of running unbounded (`~/git/llmgateway/docs/admin-api.md`; turn it on only after every
gateway instance runs v0.9.0, `~/git/llmgateway/docs/upgrade-guide.md`).

## 4. Personal data in pasted tickets

Tickets often contain names, e-mail addresses, phone numbers and personnummer. The gateway's per-key PII policy
(`~/git/llmgateway/docs/personal-data.md`) is `Off` by default; `Allow` records, `Redact` replaces personnummer,
samordningsnummer, e-mail, phone and IBAN before the request leaves the gateway, `Block` refuses, `On-prem` reroutes
(not possible without on-prem models).

This plan does not choose: the choice belongs to the DPIA (register G1) and is question 4 in [`README.md`](README.md).
Trade-offs to bring to it:

- The policy is per key, and the api key serves every assistant on the deployment, so `Redact` changes all of them,
  including the query embedding of every question (which uses the same key; harmless for retrieval, since a
  personnummer never helps a search).
- `Redact` keeps names (the gateway does not detect names).
- Ume.Rag stores no chat text in P1 (history mode `none`, Decisions "Weakness review 4"), so the gateway and the
  EU provider are where pasted ticket text travels; the frontend's own store is the other place (question 10).

## 5. Gateway-side checklist

- [ ] Answer and condense models within `Eu` listed by the gateway (condense: `gpt-6-luna` with the case budgets, §1);
      prices set (the model mirror shows cost).
- [ ] `text-embedding-3-large` within `Eu`; the platform embedding profile's canary passes.
- [ ] `api`, `admin`, `worker` keys with residency `Eu` only; Data API key with `catalog.read` (from B-35/A-30:
      credential keys, `rag-admin`'s with `organisation.read`, no Data API key).
- [ ] PII policy on the api key decided in the DPIA.
- [ ] Optional: the Foundry `Cohere-rerank-v5-fast` reranker under the Foundry provider (kind Rerank, residency `Eu`;
      gateway v0.9.1); `Settings.Retrieval.gatewayRerankModel` set to its alias.
- [ ] Optional: `requireMaxResidency` on the RAG keys.
- [ ] Optional: the embedding deployment's `ContextWindow`. [`../../../docs/gateway-host-setup.md`](../../../docs/gateway-host-setup.md)
      says to leave it empty because the gateway checked whole batches; release 1 checks embeddings per input
      (Decisions "Gateway release 1 …"), so this can be revisited (unverified on the deployed gateway).
- [ ] Keycloak realm `ume`: a client for the frontend's own sign-in, made by the realm's administrator (the gateway
      operator for the pilot; Decisions "Gateway release 1 …" Q1/Q6). See [`05-frontend-integration.md`](05-frontend-integration.md) §1.
