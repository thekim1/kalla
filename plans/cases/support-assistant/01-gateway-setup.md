# Gateway setup

What Fors's operator provides for this case. The deployment runs Källa and Fors `v0.12.2` as one Portainer stack
([`../../../docs/portainer.md`](../../../docs/portainer.md)); the Compose quick start
([`../../../docs/compose-quick-start.md`](../../../docs/compose-quick-start.md)) is for local trials. That gateway has
the residency ceiling, acting as an assigned key (`act-as-key`), rerankers on Azure AI Foundry and the Keycloak realm
shared with Källa (Decisions "Gateway keys", "Gateway release 1 and shared Keycloak as built"). Källa needs no gateway
change for the case: every need below is an existing gateway feature.

On a deployment that already serves the pilot, the models of §1 and the credential keys of §2 are checks, not new
work. DoIT's assigned key (§2) and its settings are the case's own.

## 1. Models

| Use | What | Residency | Binding rule |
|---|---|---|---|
| Answer | a chat model the gateway lists within `Eu` | `Eu` | `internal` = on-prem or EU (Decisions "Residency per level"); no on-prem models yet, so everything runs EU-hosted (Decisions "M2 and test hosting") |
| Condense (rewrites follow-up turns and long tickets into a search query; writes the follow-up suggestions) | a small chat model in `Settings.Chat.condenseModel`; **`gpt-6-luna`** (Decisions "Condense on the EU route"; user, 2026-11-06), with the budgets below | `Eu` | Decisions "Condense on the EU route"; chat §5, §5.1, §9.8 |
| Embedding | `text-embedding-3-large` at 1024 dimensions, the platform's pilot embedding profile | `Eu` | Decisions "Pilot embedding model, U-b". The knowledge base uses the platform profile; a different model would re-embed everything |
| Evaluation (question generation, judge) | any chat model in `Settings.EvaluationModel` / `EvaluationJudgeModel` | the collection's ceiling, `Eu` today | Decisions "Evaluation models at the collection's ceiling" |
| Rerank (optional) | a rerank model the gateway serves at `Eu`; Decisions "Rerank pulled forward" names none, so the model is the deployment's setting. This deployment's is `Cohere-rerank-v5-fast` on Azure AI Foundry (EU; user, 2026-11-06, Källa #421; [`README.md`](README.md) decision 12). Or a small chat model for the interim chat-model reranker | `Eu` | Decisions "Rerank pulled forward", "Gateway release 1 …": optional everywhere, off by default |

**The condense model.** It makes up to three calls per turn for this case: before the search, the condense of a
follow-up turn (budget 1.2 s) or of a long ticket (3 s); after the answer, the follow-up suggestions and, when the app
asks for `kalla.escalation` and the turn has no answer from the sources, the escalation summary (2 s each, run at the
same time; chat §9.8, §9.9). Measured on the interim gateway (chat §22, 2026-11-06, three runs each):

| Call | `gpt-6-luna` | `claude-haiku-5-5` |
|---|---|---|
| Long condense of a 16,000-character ticket | 2.3–2.9 s (tight against 3 s) | about 1.1 s |
| Follow-ups (N = 3) | 1.4–2.6 s (one run over 2 s) | 1.1–1.3 s |

**The condense model stays `gpt-6-luna`, and the budgets stay as decided** (user, 2026-11-06 and 2026-11-23;
[`README.md`](README.md) decision 13): 3 s for the long condense (Decisions "Condense on the EU route"), 2 s for the
follow-ups and the escalation summary (Decisions "Related documents and the `kalla` chat extension", "Escalation
hand-off"). Both models wrote equally good queries and suggestions. A long condense that misses its budget searches
the ticket's first 2,000 characters instead; a follow-up call that misses it gives `[]`, and a late escalation summary
is `null`. If the proof of concept or the pilot shows `gpt-6-luna` missing the budgets often, the case moves to a
faster condense model within `Eu`, such as `claude-haiku-5-5`, and keeps the budgets. Decisions "Condense on the EU
route" names `gpt-6-luna`, so that change is the user's call.

**On the local stack** (the proof of concept, [`07-rollout.md`](07-rollout.md) §0b) the model mirror is filled by
hand (model sync is off locally) and holds `claude-haiku-5-5`, `claude-opus-5-5` and `Cohere-rerank-v5-fast` today. Add
a manual mirror row for `gpt-6-luna` to try the decided condense model, or run the proof of concept on
`claude-haiku-5-5` (the measured faster alternative above) with the default budgets.

Gateway side: providers and models are added as in `~/git/llmgateway/docs/providers-and-models.md` (rerank:
section "Reranking (optional)"). Källa side, after any model change: a platform admin runs the model sync
(`POST /api/gateway/sync` in `src/Kalla.AdminApi/GatewayEndpoints.cs`) and, for an embedding change, the embedding
canary (`POST /api/gateway/canary-check`; Decisions "Embedding canary on demand"). Rerank models are set in
`PUT /api/settings/retrieval` ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Rerank
(optional)").

**The Foundry reranker.** The gateway routes rerankers on Azure AI Foundry natively since v0.9.1 (thekim1/fors#72, ADR
019; release notes "Rerankers on Azure AI Foundry, and a residency per model"):

1. Gateway admin UI: the model `Cohere-rerank-v5-fast` under the existing Foundry provider, of kind *Rerank*, with its
   residency `Eu` (the residency is per model since v0.9.1).
2. Källa: model sync, then `Settings.Retrieval.gatewayRerankModel` = that model's alias (`PUT /api/settings/retrieval`).

It was measured live on `turkos` (B-16, #421, #438); `Retrieval:GatewayRerankTimeout` defaults to 1.5 s for it
(Decisions "Gateway rerank time limit 1.5 s").

Rerank stays optional and off by default: an assistant uses it (`retrieval.rerank` `gateway`) only if the gold set
shows a gain ([`04-assistant.md`](04-assistant.md) §5). The chat-model reranker (`Settings.Retrieval.rerankModel`,
`retrieval.rerank` `chat`) remains the interim option.

## 2. Keys

| Key | Holder | Settings | Reference |
|---|---|---|---|
| One gateway **credential key** per Källa service: `api`, `admin`, `worker`, of the integration *Kalla* | the services. The gateway creates them from Källa's manifest (the Portainer stack; `--gateway-secrets-dir` with Compose); a hand-issued key file (`--gateway-credential-key-file`) overrides it | `RequireMaxResidency` on and `AllowedIpRanges` set on each (required, Decisions "Gateway keys"); residencies capped per service, the worker's at `OnPrem` + `Eu`, never `External`; PII policy `Off` or `Allow` | [`../../../docs/portainer.md`](../../../docs/portainer.md); [`../../../docs/production-setup.md`](../../../docs/production-setup.md) "Gateway credential keys" |
| The scopes `organisation.read` and `usage.read` on `kalla-admin`'s credential key (no Data API key) | the admin's organisation sync and model sync; the cost and budget read-back | – | same, "Cost and budget read-back" |
| **DoIT's assigned key** for the case: a gateway key of DoIT, chosen by id on the knowledge base and on the assistant | the gateway (Källa stores only its id; it cannot authenticate by itself) | residencies that reach `Eu`; models: the answer model, `gpt-6-luna` (condense, follow-ups, escalation), `text-embedding-3-large`, the evaluation models, and the rerank model if used; PII policy `Allow` (§4); budget and rate limits as DoIT sets them | Decisions "Cost attribution", "Gateway keys"; admin V39 |

Every assistant and every knowledge base runs on an assigned gateway key of its own department (Decisions "Cost
attribution"). Källa's service sends each call with its credential key and `x-fors-on-behalf-of: <DoIT's key id>`, and
the gateway runs it as DoIT's key: its models, residencies, PII policy, limits, budget and usage record (Decisions
"Gateway keys"; `GatewayModelCheck.ActsAsKeys` in `src/Kalla.AdminApi/GatewayEndpoints.cs`). Every call of a chat
turn (condense, query embedding, rerank, answer, follow-ups, escalation) and the admin's test panel run as the
assistant's key; indexing, re-embedding and evaluation run as the knowledge base's key. Gateway budgets and usage
therefore show the support assistant under DoIT, not under the RAG team's integration.

V39 checks the key on save and publish: it must belong to DoIT, be active, and reach at `Eu` every model the object
uses. For the assistant that is the answer model, the condense model and the linked knowledge base's embedding model
(and the rerank model when on); for the knowledge base its embedding model and the evaluation models
([`../../roadmap.md`](../../roadmap.md), "Step 3 starts on working tools"). A published assistant without a key is
refused once the gateway offers `act-as-key`.

The frontend team gets **no** gateway key: it calls Källa, never the gateway ("Every model call goes through the
gateway", `AGENTS.md`).

## 3. Residency ceiling

Källa sends `x-fors-max-residency` on every model call, derived from the level of the data in the call (identity
§4.7), capped by the service's credential key's residencies from the gateway's context (M-17 C9). The gateway bounds
an acted call by both keys: the effective residencies are DoIT's key's ∩ the credential key's, and
`RequireMaxResidency` applies when either key requires it (Decisions "Gateway keys"). An `internal` assistant therefore
calls at most `Eu`. The gateway refuses with `403 max_residency_not_met` when no target within the ceiling exists, and
answers `503 no_eligible_provider` when in-ceiling targets are drained; Källa retries only with the same ceiling
(Decisions "Gateway release 1 …"; gateway ADR 018 in `~/git/llmgateway/docs/adr.md`).

## 4. Personal data in pasted tickets

Tickets often contain names, e-mail addresses, phone numbers and personnummer. The gateway's per-key PII policy
(`~/git/llmgateway/docs/personal-data.md`) is `Off` by default; `Allow` detects and records the categories and counts
and sends the request on unchanged; `Redact` replaces personnummer, samordningsnummer, e-mail, phone and IBAN before
the request leaves the gateway; `Block` refuses; `On-prem` reroutes (not possible without on-prem models).

**Chosen: `Allow` on DoIT's assigned key: detect and log, no redaction** (user, 2026-11-23; [`README.md`](README.md)
decision 4). It is reviewed after the test chatbot's first weeks, from the gateway's *PII incidents* page filtered to
DoIT's key. Staff are told not to paste personal data, and the DPIA (G1) covers the EU-hosted model. The reasoning:

- The policy is on DoIT's own key, not on a key shared with other departments: it covers every call of DoIT's
  assistants and knowledge bases that run on that key, and nothing else on the deployment.
- `Allow` shows how often tickets carry personal data without changing any text. `Redact` would also rewrite the query
  embedding of every question and, if the knowledge base runs on the same key, the text it indexes; `Block` would
  refuse whole tickets.
- `Redact` keeps names (the gateway does not detect names), so it would not make tickets free of personal data.
- Källa stores no chat text in P1 (history mode `none`, Decisions "Weakness review 4"), so the gateway and the
  EU provider are where pasted ticket text travels; the frontend's own store is the other place (question 10). The
  gateway stores only categories and counts, never the values.

## 5. Gateway-side checklist

- [ ] Answer and condense models within `Eu` listed by the gateway (condense: `gpt-6-luna`, §1); prices set (the model
      mirror shows cost).
- [ ] `text-embedding-3-large` within `Eu`; the platform embedding profile's canary passes.
- [ ] `api`, `admin`, `worker` credential keys of the integration *Kalla*, each with `RequireMaxResidency` and
      `AllowedIpRanges`, the worker's capped at `OnPrem` + `Eu`, `kalla-admin`'s with `organisation.read` and
      `usage.read`; no Data API key.
- [ ] DoIT's assigned key: residencies reaching `Eu`; the answer, condense, embedding and evaluation models (and the
      rerank model if used); PII policy `Allow`; budget. Chosen on the knowledge base and the assistant (V39 passes).
- [ ] Optional: the Foundry `Cohere-rerank-v5-fast` reranker under the Foundry provider (kind Rerank, residency `Eu`);
      `Settings.Retrieval.gatewayRerankModel` set to its alias.
- [ ] Optional: the embedding deployment's `ContextWindow`. The first set-up guide
      said to leave it empty because the gateway checked whole batches; release 1 checks embeddings per input
      (Decisions "Gateway release 1 …"), so this can be revisited (unverified on the deployed gateway).
- [ ] Keycloak realm `fors`: a client for the frontend's own sign-in, made by the realm's administrator (the gateway
      operator for the pilot; Decisions "Gateway release 1 …" Q1/Q6). See
      [`05-frontend-integration.md`](05-frontend-integration.md) §1.
