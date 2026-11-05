# Identity, authentication and authorisation: plan

How people, applications, chat clients, MCP clients and downstream systems are identified and authorised across the
platform. The pilot (P1) signs in with a **Keycloak shared with the LLM gateway**; the municipality's **Entra ID**
follows in P2; each system's own Keycloak serves local development and CI, as in the gateway (§1; Decisions table
"P1 pilot sign-in"; identity design §11.2, 2026-10-31). Cross-cutting: used by every service. **Detailed design: [`../../identity/design.md`](../../identity/design.md)** (step 3); where they differ,
the design wins.

**Guiding rule (2026-10-29): be as compatible as possible with the LLM gateway.** Same identity model, same claim
names and meanings, same configuration keys, same session and audit behaviour, same patterns for keys, machine
clients and security events (`~/git/llmgateway/docs/identity-and-roles.md`, `data-access.md`,
`security-events.md`). Where the RAG platform needs something the gateway lacks, it is added to **both**, not
invented on one side.

## 1. Principles

1. **An OIDC provider is the identity source**; Keycloak, AD FS and Entra ID work the same way, as in the gateway.
   **Pilot (P1): one Keycloak shared with the LLM gateway** (realm `ume`, its own Compose project on the gateway's
   production host, defined in the gateway repo) holds the admins' and pilot users' accounts (local accounts, admins
   with OTP; LDAP federation to the municipal AD later), and the admin UI and Open WebUI sign in through it (user,
   2026-10-30: as simple as possible, no Entra approval on the pilot's critical path; Decisions table). **Entra ID from P2** (approval package 1, `infra/entra/`, is ready but not submitted), a
   configuration change, not code. The RAG services store no passwords; Keycloak does, for the pilot only.
2. **Every request has a subject and a client.** The subject is a user, an application or an anonymous visitor; the
   client is the front end or integration it arrived through. Both are part of every authorisation decision.
3. **One policy engine** (`IAccessPolicy` in `Ume.Rag.Domain`) used by the Chat/Knowledge API, the MCP server, the
   tool hub and the admin.
4. **Fail closed**: security-relevant configuration is used only from a snapshot confirmed within
   `Config:MaxUnconfirmedSeconds` (90 s; platform plan §4); beyond that, keyed requests get 503.
5. **Least privilege downstream**: e.g. `Sites.Selected` instead of `Sites.Read.All`.
6. **No approval blocks development**: local development, CI and most of P1 run on Keycloak and keys.

## 2. When Entra registrations are actually needed

| Need | Works without an Entra registration? | Needed from |
|---|---|---|
| Development and CI | Yes: Keycloak with the same claims | never |
| Platform → gateway | Yes: the pilot team's gateway keys per level until gateway release 2 (P2), then delegating keys per level (`gateway-changes.md` §4; weakness review §5) | never |
| Open WebUI → Chat API | Yes: gateway-style key (`ume-rag-sk-…`) for the connection + Open WebUI's signed user JWT | never |
| Integrations → our APIs | Yes: keys | only if an app prefers OAuth tokens |
| URL lists, crawling, uploads | Yes | never |
| **Staff sign-in to the admin UI** | Yes: the shared Keycloak (client `ume-rag-admin`, local accounts, admins with OTP; design §11.2) | P2 (Entra replaces Keycloak) |
| **Pilot users' sign-in to Open WebUI** | Yes: the shared Keycloak (client `open-webui`; design §11.2) | P2 (Entra replaces Keycloak) |
| Group- or department-based assistant audiences for chat users | No | P2 (pilot audience = "users of this client") |
| Graph lookup of member groups the claim does not show | No | P2 (package 2) |
| `rag-api` scopes (own chat client, MCP server, OBO) | No | P2–P3 |
| SharePoint (`rag-ingestion`) | No | P2 |

**Approval packages** (created by the deployment pipelines as infrastructure as code, approved by a person):

- **Package 1, for P2** (kept but not submitted; H7 deferred; the pilot signs in with the shared Keycloak, Decisions
  table "P1 pilot sign-in", 2026-10-31): RAG's **own** registrations, created and owned by
  RAG's Bicep (`infra/entra/`), which never touches the gateway's registrations (decided 2026-10-30,
  `../passes/simplification-pass.md` §7):
  - *Admin sign-in*: an `ume-rag-admin` registration configured like the gateway's (`identity-and-roles.md`, *Entra
    ID* steps 1–5; same code), with the RAG admin's redirect URIs, the `rag-*` app roles (§3), the group claim for
    member groups (§3) and its own client secret (created by an operator, as the gateway's). No Graph application
    permission.
  - *Open WebUI sign-in*: an `open-webui` registration (its own app).
  - Pilot groups and their assignments.
- **Package 2, submitted during P1, for P2**: `rag-api` (scopes and app roles), `rag-ingestion` (`Sites.Selected`),
  `ume-rag-tools` (the tool identity), `rag-chat-client`, the first municipal MCP server (`ume-mcp-kommun`), `GroupMember.Read.All` on `ume-rag-admin` for the
  Graph member-group lookup.
- **Later**: OBO downstream consents (P3). Per-site `Sites.Selected` grants are made by admins without the slow
  approval (answered question 2).

On Azure, daemons use managed identities or workload identity federation; on-prem, certificates rather than secrets.
Only tokens from the municipality's own tenant are accepted (`tid` check); guest accounts are refused, with one rule
in both systems (design §1.7). The rule takes effect with Entra (P2); against the pilot's Keycloak the tenant and guest
checks are no-ops (design §1.7, §11.2).

## 3. People in the admin UI (same model as the gateway)

**Mechanism**: the gateway's BFF pattern unchanged: OIDC code + PKCE, secure HttpOnly cookie, CSRF, 8-hour session,
extend before expiry, never more than `Admin:MaxSessionHours` (12) from sign-in; audit actor is the **`oid`** claim
(fallback `sub`), which is the same user id in every registration of the tenant; on the P1 Keycloak there is no `oid`,
so the actor is `sub` (design §1.1).

**Claims**: exactly the gateway's names and meanings:

| Claim | Meaning (same as the gateway) |
|---|---|
| `roles` | Role values (below) |
| `departmentCodes` | Cost-centre codes of the user's departments; matched against the department's cost-centre code from the gateway |
| `teamCodes` | Team codes: from P2, owner-team scoping of admin writes where a department switches it on (design §1.3); no team-scoped role |
| `name`, `email`, `oid`, `sub` | Display and audit (`oid`, fallback `sub`) |

**Role values** are namespaced (`rag-*`), so they can never be mistaken for gateway roles:

| Role value | Meaning in the RAG platform |
|---|---|
| `rag-platform-admin` | Everything (like `gateway-admin`) |
| `rag-department-admin` | Everything in own departments (like `department-admin`) |
| `rag-pipeline-editor` | Pipeline editing in own departments (template forms in P1, canvas in P2) |
| `rag-reviewer` (P1) | Reads feedback comments and, with a reason (audited; `sensitive` with a second approver), conversations in own departments; not implied by any admin role (design §1.3, admin design §3.1). The role is defined in P1 but has nothing to read while the pilot runs history mode `none`; conversation review is built when A1 allows stored content (weakness review §4) |
| `rag-viewer` | Read status, runs, evaluation, usage in own departments (like `viewer`) |
| `rag-skill-editor` (P2) | Drafts, publishes and serves skills in own departments (tools design §3.6) |
| `rag-editor` (from P2) | Sources, collections and assistants via forms in own departments; links published skills, cannot edit them |

**Owner-team scoping (P2, `../passes/simplification-pass.md` §15)**: assistants, collections and skills keep an owner team; a
department may limit editors' writes to members of the owner team (user's `teamCodes` matched to the team's code).
Department and platform admins still see and edit everything. It is an admin rule, not a role and not part of
`IAccessPolicy` (design §1.3).

**Configuration keys**: the gateway's OIDC settings with a `UME_RAG_` prefix and the same defaults and semantics:
`UME_RAG_OIDC_AUTHORITY`, `_CLIENT_ID`, `_CLIENT_SECRET` (file, as `init-deployment.sh` does), `_ROLE_CLAIM`
(`roles`), `_DEPARTMENT_CLAIM` (`departmentCodes`), `_TEAM_CLAIM` (`teamCodes`), `_GROUP_CLAIM` (`groups`),
`_GROUPS_<ROLE>` (groups that grant a role, `;`-separated), `_EXTRA_SCOPE`.

**Roles in P1**: realm roles `rag-*` in the shared Keycloak realm `ume`, granted per account and emitted in `roles` by
the `ume-rag-admin` client's role mapper (full scope allowed off, role scope mappings to the `rag-*` roles; design
§11.2, 2026-10-31).

**Roles from Entra (P2)**: as the gateway recommends, **app roles assigned to groups** (`roles` claim), so no token group
lists and no 200-group overage. Group mapping through `UME_RAG_OIDC_GROUPS_<ROLE>` (object ids) works as an
alternative, exactly as in the gateway.

**Departments without a department attribute.** Both systems get department scope only from `departmentCodes`, and
the municipality has no such attribute today (answered: users are organised in groups). Rather than a RAG-only
mapping, the same small feature is added to **both systems** (see `gateway-changes.md`, feature C), **claims
only**. It reaches production with **gateway release 2 in P2** ([weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)): only then does our mirror get member groups
from the gateway's catalogue; until then they are entered by hand (below). The resolver itself is built and tested in P0 against the FakeGateway catalogue.

- A department (in the gateway's *Organisation* page, and mirrored here because organisation data comes from the
  gateway) may list **member groups** (group claim values).
- At sign-in, a user's department scope = codes from the `departmentCodes` claim **plus** departments whose member
  groups appear in the user's group claim. Their *role* still comes from `roles`.
- A group the token does not show adds nothing (scope can only narrow). A Graph lookup for such groups is P2 (§2).
- When IT later adds a department attribute, the claim takes over without any code change.
- **Until release 2** (P1), department scope comes from the `departmentCodes` claim, plus **member groups entered by
  hand on our own department record** (weakness review §5): a platform admin enters the pilot department's
  group claim values with `PUT /api/organisation/departments/{id}/member-groups` (admin design §4.5; P1: Keycloak
  group names, design §2.2, §11.2; Entra object ids from P2), and our resolver matches them against the `groups` claim exactly as
  it will later with synced data. With release 2 the org sync from the gateway catalogue fills the same field and the
  hand-entered values are replaced. The gateway itself is unchanged; platform admins need no department scope.

Groups are stored as group claim values: Entra object ids (works for cloud groups now and synced AD groups later,
answered question 1), group names on Keycloak/AD FS (design §2.2). In P1 they are entered as values; picking groups
by name search comes with the Graph lookup in P2 (there is no naming convention, question 5).

## 4. Callers of the data-plane APIs

| Caller | Credential | Subject | Client (for ceiling) | Phase |
|---|---|---|---|---|
| Open WebUI | Connection key + HS256 JWT forwarded by Open WebUI (`X-OpenWebUI-User-Jwt`) | User (email/`sub` from Open WebUI) | The Open WebUI instance | P1 |
| Integrations | **Keys** created by admins (gateway virtual-key style) or **OAuth client-credentials tokens** (gateway Data API style, app roles `Chat.Invoke.All` / `Knowledge.Read.All`) | Application | The key or `azp` | P1 keys / P2 tokens |
| Public widget | `ume-rag-pk-` key + allowed origin | Anonymous session | The widget instance | P2 |
| Our chat client / Teams | Entra access token, `aud = rag-api`, scope `Chat.Use` | User (`oid`) | `azp` | P2/P3 |
| MCP clients | Entra access token, scope `Mcp.Use` (§6) | User | `azp` | P2 |

**Keys** follow the gateway's virtual keys: generated secret shown once, only an HMAC and a display prefix stored,
rotation with a grace period, reveal (if ever) audited. They use a **distinct prefix** from gateway keys, so the two
can never be confused or accepted by the wrong system.

**OAuth for machines** follows the gateway's Data API configuration: `RagApi:Authority`, `RagApi:Audience`
(default `ume-rag-api`), `RagApi:PermissionClaims` (default `scope`, `scp`, `roles`), so Keycloak, AD FS and Entra
work the same way and integrations configured for the gateway's Data API need nothing new.

Token validation: issuer and tenant, signature (cached JWKS), audience, expiry, scope or role. Open WebUI JWTs: shared
secret, short expiry, only from the Open WebUI instance's network.

## 5. Authorisation model

**Resources**: collections, assistants, skills, tool servers/tools, conversations, admin objects.

**Assistant audience** (who may use it):

- **P1**: `clients` only: e.g. "everyone who can sign in to the pilot's Open WebUI". Open WebUI's own login against the
  shared Keycloak decides who that is (who may sign in is open, identity design §16 question 5); from P2 Entra with
  *assignment required* on its enterprise app. No Graph lookup needed.
- **P2**: also `departments` (department scope as in §3) and `groups` (group claim values), for callers with real
  tokens. For Open WebUI users they wait for E1 (a real Entra token through Open WebUI, §7) and W3 (Open WebUI's
  process-wide model list); Open WebUI's e-mail is never the basis of a grant.
- **Beyond one department in Open WebUI** ([weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)): until W3 and E1 are solved, each department gets **its own Open
  WebUI instance**, registered as its own client with its own ceiling, and its assistants' audience is that client.
  The spike on the W3 workaround (Open WebUI group permissions) runs in P1b; E1 is P2, with Entra (Decisions table
  "P1b briefs", O3).

**Assistant versions** (`../passes/simplification-pass.md` §15): audience and classification belong to the assistant, not to
its versions, so publishing or restoring a version never changes who may use it; only the published version reaches
the data plane (design §4.3).

**Decision** = subject may use the resource (audience) **and** the resource's classification ≤ the client's ceiling
**and** the subject type is allowed (no anonymous access to `internal`) **and**, for tools, the tool policy allows it.

**Client registry** (review R5): every client has an id, a classification ceiling and a data-handling profile. Pilot
defaults: own chat client `sensitive`, Open WebUI `internal`, widget `public`, integrations as configured. The
Open WebUI pilot client keeps `internal` only for EU providers covered by a processor agreement (PUB-avtal) and listed
in the DPIA (register G1); if the DPIA requires more, it is limited to on-prem models by model choice, not by the
registry (weakness review §4; design §4.1). With no on-prem hardware yet (register M2) that fallback does not exist
(accepted risk, Decisions table "P1a plan answers").

- **MCP clients: `public` by default**; a platform admin may raise a vetted client to `internal`, never `sensitive`.
- **Clients that keep their own copy of conversations** (Open WebUI, Teams) have a ceiling of at most `internal`.

**Explain access**: `POST /api/access/explain` takes (client, subject, assistant) and returns the stable id of the
first failing policy step and its reason code (design §4.9); cheap because `IAccessPolicy` is pure, and no security
event (nothing is accessed). Platform admins, and department admins for their own department's assistants and
clients.

**Security events** use the gateway's scheme: a `Ume.Rag.Security` log category with stable event names and numbers
(a separate number range from the gateway's), sent over OpenTelemetry, so the same collector rules route both
systems to the SIEM. Every denial is a security event.

## 6. MCP authorisation (platform as MCP server, P2)

- The MCP endpoint is an OAuth **protected resource** publishing Protected Resource Metadata (RFC 9728) pointing at
  Entra; tokens need `aud = rag-api` and scope `Mcp.Use`.
- Entra has no dynamic client registration, so each MCP client is **pre-registered** and added to the client
  registry with a ceiling. Newer registration options (Client ID Metadata Documents) in Entra and MCP clients: to verify.

## 7. Downstream access: service identity now, on-behalf-of later

- **P2, service identity**: the in-process tool hub calls hosted MCP servers with client credentials of the tool
  identity `ume-rag-tools`, whose certificate only `rag-api` and `rag-admin` hold (never the worker). Read-only tools
  only. A separate `rag-tools` service comes only with the first external, OBO or write tool (P3).
- **P3, on-behalf-of**: the tool hub exchanges the user's `rag-api` token for a downstream token with Entra OBO.
  - Needs a real Entra token: our own client, Teams and MCP clients send one; Open WebUI can too with
    `auth_type: system_oauth` if its login requests `api://rag-api/Chat.Use`
    (`open-webui/backend/open_webui/utils/headers.py:209-212`; register E1, spiked in P2 with Entra).
  - **Decided**: the exchange uses the `rag-api` registration's credential (Entra requires the token to be issued to
    the app doing the exchange); `ume-rag-tools` stays for service-identity calls.
  - The user token is an internal, short-lived hand-off from the Chat API, never stored or logged; OBO caches are
    process-local in the tool hub (no Redis), per user and short-lived (design §9.2); claims challenges are passed back to the client.
- Non-Entra per-user OAuth for third-party tools: out of scope until a concrete need appears.

## 8. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Keycloak realm with the gateway's claims for dev and CI; OIDC and claim mapping copied from the gateway (`UME_RAG_OIDC_*`); keys in gateway style; package 1 built as Bicep, kept for P2, not submitted |
| P1 | Admin sign-in on the shared Keycloak (`ume-rag-admin`, realm `ume`; Entra in P2); `rag-reviewer` role; department scope from the `departmentCodes` claim plus member groups entered by hand; Open WebUI sign-in and JWT trust; client registry with ceilings; client-based audiences; explain access; security events; package 2 submitted; P1b: the spike on the W3 workaround (weakness review §5) |
| P2 | Department scope from groups in the claim with gateway release 2 (feature C, both systems; weakness review §5), `rag-api` tokens and OAuth machine clients, department/group audiences (one Open WebUI instance per department until W3 and E1 are solved), the E1 spike, Graph member-group lookup, owner-team scoping of admin writes, MCP server authorisation, `rag-ingestion`, tool identity `ume-rag-tools` (in-process hub), `ume-rag-pk-` widget keys |
| P3 | OBO for user-delegated tools (with the `rag-tools` service), Teams client, claims-challenge handling |

## 9. Answered questions (2026-10-29)

1. **Group type**: start with Entra cloud groups; synced AD groups later work unchanged (object ids).
2. **SharePoint site grants**: made by admins without the slow approval; only `rag-ingestion` needs approval.
3. **Guests**: no; only accounts in the municipality's tenant.
4. **Citizen login**: out of scope for now.
5. **Group naming**: no convention; groups are picked by search once the Graph lookup arrives (P2); in P1 they are
   entered as values (Keycloak group names on the shared realm, design §2.2).
6. **Approval timing**: no registration is needed for P0 or development; package 1 (admin and Open WebUI sign-in)
   only has to be approved before the pilot. **Revised 2026-10-30:** the pilot signs in with the shared Keycloak, so
   package 1 is needed only for P2 (Decisions table "P1 pilot sign-in").

7. **Admin registration** (revised 2026-10-30, simplification pass): RAG gets its own `ume-rag-admin` registration
   (same code as the gateway's), with its own credential.
8. **Registrations as code**: `ume-rag/infra/entra/` (Bicep) owns only RAG's registrations, never the gateway's;
   admin consent, role assignments and client secrets stay manual approval steps (design §12).
9. **Admin credential type**: a client secret of our own, identical to the gateway's setup; certificates later for
   both systems together.
10. **Many assistants per department** (2026-10-30, simplification pass §15): owner-team scoping of admin writes in P2
    from `teamCodes` (§3); assistant versions do not change access (§5).

## 10. Deferred or removed (simplification pass)

Per [`../passes/simplification-pass.md`](../passes/simplification-pass.md) §1, §6, §7 and §9:

| Removed or deferred | Where it went |
|---|---|
| RAG admin on the gateway's existing admin registration (and the twin fallback); RAG's Bicep owning the gateway's registration | removed: own `ume-rag-admin` registration, Bicep owns only RAG's registrations (§2) |
| Graph `checkMemberGroups` lookup, `GroupMember.Read.All` in package 1, membership cache, circuit, its security events, the "failed" banner, `OrgSync:MaxAgeHours` | P2, with data-plane audiences and package 2 (§2) |
| Open WebUI e-mail as the basis of grants (`ResolveUsersByEmail`) | dropped; group audiences through Open WebUI wait for E1 (§5) |
| Audit actor `sub` | `oid`, fallback `sub` (§3) |
| Strict "current config version" rule (10 s confirmation) | grace window `Config:MaxUnconfirmedSeconds` (§1) |
| `rag-tools` as a P2 service holding the tool credential | P3 with the first external, OBO or write tool; in P2 `rag-api` and `rag-admin` hold the `ume-rag-tools` certificate (§7) |

## 11. Deferred or removed (weakness review)

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| Department scope from groups in the claim in production (feature C, both systems) | P2, with gateway release 2; the resolver is built and tested in P0, and in P1 it matches member groups entered by hand (§3) |
| Delegating gateway keys for the pilot | the pilot team's per-level keys until release 2 (§2) |
| E1 test (OBO through Open WebUI) and the W3 workaround | W3 spike in P1b; E1 in P2 with Entra (Decisions table "P1b briefs", O3); fallback decided: one Open WebUI instance per department (§5) |
