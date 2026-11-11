# Keys and budgets per assistant: options

Whether an assistant should have keys of its own, and how a cost limit per assistant should work with the LLM gateway.
Status: **options for a decision, not binding** (main session, 2026-11-10). Nothing here changes a design. The
decision that binds today is Decisions "Cost attribution (user, 2026-11-08)" and
[`cost-attribution.md`](cost-attribution.md); this document reopens its option A and its "team per assistant"
convention.

Gateway facts are from `~/git/llmgateway` at `origin/main` v0.9.2 (`df6bf5e`), read on 2026-11-10.

## 1. Two different questions

"A key per assistant" means two things, and they are decided separately.

| | Inbound | Outbound |
|---|---|---|
| Whose key | Ume.Rag's own (`ume-rag-sk-…`) | the gateway's (`ume-sk-…`) |
| Who presents it | an app that calls an assistant through rag-api | Ume.Rag, when it calls a model |
| What it decides | who may use the assistant, from where, how often | who pays, which budget counts, which models are allowed |
| Section | §3 | §4–§6 |

The two key stores are separate and stay so (identity §6.1: different prefixes, different peppers; each system
refuses the other's keys). The caller's key decides access. It never decides who pays: that follows from the
assistant.

## 2. Requirements

| # | Requirement | Source |
|---|---|---|
| R1 | Departments pay for their own use; the gateway is the record | cost-attribution §1 |
| R2 | A department can give one assistant a hard cost limit | cost-attribution §1 |
| R3 | **All budgets live in the gateway. No second system controls an assistant's budget** | user, 2026-11-10 |
| R4 | Editors see what each assistant costs | cost-attribution §1 |
| R5 | An app can be given access to one assistant, and lose it, without touching other apps | support-assistant case |

R3 is new and rules out option E (§5).

## 3. Inbound: Ume.Rag keys that reach one assistant

### 3.1 What exists

- A key belongs to a **client**, has an optional **team**, and `Scopes.AssistantIds` / `Scopes.CollectionIds` narrow
  it (`src/Ume.Rag.Domain/Entities/KeyEntities.cs`; identity §4.3 step 6, a miss is 404 `key_scope`).
- Per key: `AllowedIpRanges`, `RequestsPerMinute`, expiry, rotation (immediate or 24 h grace), audited reveal, revoke
  (`src/Ume.Rag.AdminApi/KeyEndpoints.cs`; identity §6.4).
- V23 limits scopes to assistants and collections of the key team's department (admin §4.6).
- The support-assistant case plans one `integration` client and one key scoped to its assistant
  (`plans/cases/support-assistant/05-frontend-integration.md`).

### 3.2 What is missing

| # | Gap | Evidence |
|---|---|---|
| I1 | No key UI on the assistant page. Keys are only in the generic platform form (`/plattform/keys`), where the admin picks a client and enters assistant ids | `src/admin-ui/src/platform/resources.ts`; no key code in `src/admin-ui/src/views/AssistantView.vue` |
| I2 | A department admin may manage keys of own teams, but a key needs a client, and clients are platform-admin objects | identity §6.4; admin §5.8 |
| I3 | Usage is recorded per day, assistant and **client**, not per key | `AssistantDailyTotals`; no key id found in `src/Ume.Rag.Domain/Entities/ChatEntities.cs` or `src/Ume.Rag.Api/ConversationWriter.cs` (grep only, not a full read) |

### 3.3 Options

| Option | What | For | Against |
|---|---|---|---|
| **I-a. Leave as is** | platform admin creates scoped keys in the generic form | no work | every key is a ticket to the platform team; nobody sees which keys reach an assistant |
| **I-b. "API access" tab on the assistant** | lists the keys that reach this assistant; *Create key* pre-scopes to it, team = owner team; rotate and revoke there | self-service, visible, no model change | I2 must be settled (below); a little admin API (list keys by assistant) |
| **I-c. One fixed key per assistant** | the assistant has exactly one key, as some platforms do | simplest to explain | two apps sharing it cannot be revoked, limited or measured apart; fails R5 |

**I2, to settle with I-b:** either the tab only picks an `integration` client a platform admin has made, or a
department admin may create an `integration` client (its ceiling capped by the department's highest level). A client
carries the classification ceiling, history storage and tool policy, so letting departments create them widens what
a department admin controls.

**Recommended:** I-b, with one key per consuming app per assistant, and the key id added to the chat totals (I3) as
part of cost-attribution step C1.

## 4. Outbound: what the gateway offers today

| Fact | Where |
|---|---|
| Budgets have three scopes: department, team, key. USD or tokens. Periods hourly to yearly. A spent budget answers 402 and names the scope | `src/Ume.LlmGateway.Domain/BudgetEnums.cs`, `Entities/Budget.cs`, `Gateway/Budgets/BudgetService.cs` |
| A usage record holds the key, its team and its department. Nothing per request (no tag, no end user) | `src/Ume.LlmGateway.Domain/Entities/UsageRecord.cs` |
| A key carries its own model, provider and residency allow-lists, IP ranges, rate limits and `RequireMaxResidency` | `src/Ume.LlmGateway.Domain/Entities/VirtualKey.cs` |
| The admin API (create a key, set a budget) is cookie sign-in only. No machine credential can create keys | gateway `docs/admin-api.md` |
| Delegation (`x-ume-on-behalf-of`) is designed for release 2 and **not built** | [`../gateway-changes/design.md`](../gateway-changes/design.md) §5 |
| Under the delegation design, team and department budgets follow the named team; key budgets stay with the delegating key, so a key budget would cap the whole platform, not one assistant | gateway-changes §5.4 |
| Ume.Rag today: keys per level and deployable, secret files, owned by the pilot team. All chat traffic is one line in the gateway | Decisions "Gateway keys"; gateway-changes §13.4 |

So a per-assistant budget needs something in the gateway, one per assistant, that a budget can attach to. Today that
can be a team or a key. Anything else is new gateway work.

## 5. Outbound options

### A. A gateway team per assistant (the current plan)

Delegating platform keys per level; every call sends `x-ume-on-behalf-of: <owner team id>`. A department that wants a
limit on one assistant creates a gateway team for it (e.g. *DoIT › Supportassistenten*), sets the budget on that team
and makes it the assistant's owner team (cost-attribution §4.2).

- **Needs:** gateway release 2 (feature A); Ume.Rag steps B2–B6 of cost-attribution §6.
- **For:** already decided and designed; no secret per assistant; the gateway is the only enforcer (R3); works for
  ingestion too.
- **Against:**
  - Teams that are not teams appear in the gateway's organisation, in its reports and in Ume.Rag's team pickers.
  - The owner team has a second meaning. With `DepartmentSettings.EditOwnerTeamOnly` (P2, admin §3.2) an edit needs
    membership of the object's owner team. A per-assistant team has no real members, so the budget use and the
    edit-scope use collide.
  - Each delegating key lists the teams it may act for (`DelegatedTeamIds`), so every new per-assistant team is also a
    change to those lists (cost-attribution §7 Q2).
  - Not available until release 2.
- **Variant A2:** split the two meanings: keep `OwnerTeamId` for edit scope and add a `BillingTeamId` that is sent as
  `x-ume-on-behalf-of`. Removes the collision; the fake teams remain.

### B. A real gateway key per assistant

Each assistant that needs it has its own gateway key, owned by its team in the gateway. Ume.Rag uses that key for the
assistant's calls. The budget is an ordinary key budget.

- **Needs:** no gateway change. Ume.Rag: a place to hold the key and a rule for picking it per call.
- **For:**
  - Works today: key budgets and per-key usage exist.
  - The cost centre follows without delegation, because the key belongs to a team and department.
  - Least privilege: the key can allow only that assistant's models and residency level.
  - The gateway is the only enforcer (R3).
- **Against:**
  - Ume.Rag holds one secret per assistant. The secret adds no security (Ume.Rag holds them all); it is only a label.
  - A manual gateway step for every such assistant: create, hand over, rotate.
  - A department admin can disable or revoke the key in the gateway and the assistant stops. Manageable with a clear
    error and an attention item, but it is a new failure mode.
  - Ingestion is not covered (it belongs to a knowledge base); it stays on the platform key.
- **How the earlier rejection reads now** (cost-attribution §3, option A):

  | Reason given | Assessment |
  |---|---|
  | Against "Gateway keys" (secret files, not database rows) | our own rule, not a constraint from outside; variant B1 keeps it |
  | A secret per assistant, level and deployable | probably overstated: an assistant mostly runs at one level, and api and admin can share the key. Not checked against identity §4.7 in detail |
  | A manual gateway step per assistant | true today (cookie-only admin API) |
  | Rotation per key | true; grows with the number of assistants |
  | A department admin can break an assistant unseen | weak; an attention item covers it |
  | Ingestion still on the platform key | true, but ingestion is not per assistant in any option |

- **Variants:**
  - **B1, secret file per assistant:** an optional deployment knob (a key per assistant slug, as a secret file). Keeps
    the "secret files" rule. Operator work per assistant; fits a handful, not hundreds.
  - **B2, stored encrypted:** the key in the database under Data Protection, entered on the assistant page by a
    platform admin, as Ume.Rag's own keys are stored for reveal. Self-service; a database and key-ring leak then
    exposes gateway keys. Changes Decisions "Gateway keys".
  - **B3, provisioned:** the gateway gains a machine API to create and rotate keys, and Ume.Rag calls it. No manual
    step; new gateway work, and Ume.Rag then holds a credential that can mint keys.

### C. A budget line under a team, named by the delegated call

Change the release 2 design so a delegated call names the owner team **and**, optionally, a budget line under it. The
gateway counts and stops on the line as well as on the team and the department. An assistant without a line counts
against its team only.

- **Needs:** a change to gateway-changes §5 before release 2 is built: a fourth budget scope, a column on the usage
  record, a second header or a second part of `x-ume-on-behalf-of`, the echo, the UI. Ume.Rag: the assistant stores
  the line's id (mirrored by the organisation sync) and sends it.
- **For:**
  - The gateway is the only enforcer (R3), with no secret per assistant and no fake teams.
  - The owner team keeps one meaning.
  - Per-assistant figures appear in the gateway's own reports (R4 without relying on Ume.Rag's estimate).
  - Cheap to change now, since release 2 is not built.
- **Against:**
  - New gateway concept and work, on top of release 2.
  - Someone still creates the line in the gateway (a department admin, by hand) unless a provisioning API is added.
  - Not available until release 2.
- **Variants (the gateway owner's call; the gateway code was not read closely enough to say which is smaller):**
  - **C1, a new entity** (working name *budget line*): belongs to a team, has budgets, appears on usage records.
  - **C2, a key that cannot sign in:** reuse `VirtualKey` as the line, so key budgets, the key budget UI and per-key
    reports work unchanged, and the delegated call is processed as that key. To check first: per-key rate limits,
    session affinity, `LastUsedAt` and the allow-lists would then be the line's, not the platform key's.

### D. A free tag on gateway usage records

The assistant id in a header, stored on the usage record.

- **For:** small gateway change; per-assistant reports in the gateway.
- **Against:** reporting only. A tag has no budget, so R2 is not met. Rejected on 2026-11-08 and nothing has changed.

### E. Ume.Rag enforces the budget itself

A budget field on the assistant; chat checks Ume.Rag's own metered total before each call.

- **For:** no gateway dependency; set on the assistant page; available as soon as stage 1 metering is.
- **Against:** a second system controls the budget, with figures that differ from the gateway's (mirror prices, no
  view of fallbacks and retries).
- **Ruled out by R3** (user, 2026-11-10). Ume.Rag's metering stays as reporting only, as already decided.

## 6. Comparison

| | A team per assistant | B key per assistant | C budget line | D tag | E Ume.Rag budget |
|---|---|---|---|---|---|
| Hard limit per assistant (R2) | yes | yes | yes | no | yes |
| Gateway is the only enforcer (R3) | yes | yes | yes | – | **no** |
| Works before gateway release 2 | no | **yes** | no | no | yes |
| New gateway work beyond release 2 | none | none (B3: provisioning API) | a budget scope | a column | none |
| Secrets Ume.Rag holds | few (per level) | one per assistant | few (per level) | few | few |
| Manual gateway step per assistant with a limit | create a team, extend the delegation lists | create a key, hand it over | create a line | none | none |
| Organisation stays truthful | no (fake teams) | yes | yes | yes | yes |
| Owner team keeps one meaning | no (A2: yes) | yes | yes | yes | yes |
| Per-assistant figures in the gateway's reports | yes (as a team) | yes (as a key) | yes | yes | no |
| Covers ingestion cost | yes | no | yes (team level) | – | – |

## 7. Recommendation (main session)

1. **Inbound:** I-b, an "API access" tab on the assistant, one key per app per assistant.
2. **Outbound, long term:** C. Keep delegation for who pays; add a budget line for assistants that need their own
   limit. If the gateway owner prefers no new concept, A2 is the fallback.
3. **Outbound, before release 2:** only if an assistant needs a hard limit that early, B1 for that assistant. The plan
   says hard budgets are not needed during the pilot (cost-attribution §7 Q3), so this may never be used.
4. **Not:** D and E.

## 8. Questions for the decision

| # | Question | Depends on |
|---|---|---|
| Q1 | Outbound long term: A (as decided), A2, or C? | the gateway owner's view of a fourth budget scope |
| Q2 | If C: a new entity (C1) or a key that cannot sign in (C2)? | a read of the gateway's key pipeline |
| Q3 | Is a hard limit per assistant needed before gateway release 2? If yes: B1 or B2? | pilot department |
| Q4 | Who creates the gateway object for a limited assistant: a department admin by hand, or Ume.Rag through a gateway provisioning API? | gateway roadmap |
| Q5 | Inbound: may a department admin create an `integration` client, or only pick one a platform admin made? | §3.3 |
| Q6 | Should Ume.Rag's assistant page show the gateway's budget status read-only (Data API)? | Q1 |

## 9. What a decision changes

| Decision | Documents to update |
|---|---|
| I-b | admin §5.5 (the tab) and §4.5 (list keys by assistant); identity §6.4 (who creates what); cost-attribution §5 C1 (key id in the totals) |
| A2 | data-model §2.1 (`BillingTeamId`); cost-attribution §4.2; identity §4.7 |
| B1 or B2 | Decisions "Gateway keys" and "Cost attribution"; identity §4.7; repo-and-deployment (secret files) or data-model §2.1 |
| C | Decisions "Cost attribution"; gateway-changes §5 and ADR 020; cost-attribution §4 and §6; data-model §2.1; the organisation sync (admin §4.10) |
