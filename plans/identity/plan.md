# Identity, authentication and authorisation: plan

How people, applications, chat clients, MCP clients and downstream systems are identified and authorised across the
platform. The municipality uses **Entra ID**; Keycloak is used for local development and CI, as in the gateway.
Cross-cutting: used by every service.

**Guiding rule (2026-10-29): be as compatible as possible with the LLM gateway.** Same identity model, same claim
names and meanings, same configuration keys, same session and audit behaviour, same patterns for keys, machine
clients and security events (`~/git/llmgateway/docs/identity-and-roles.md`, `data-access.md`,
`security-events.md`). Where the RAG platform needs something the gateway lacks, it is added to **both**, not
invented on one side.

## 1. Principles

1. **The municipal OIDC provider is the identity source** (Entra ID in production; Keycloak or AD FS work the same
   way, as in the gateway). The platform stores no passwords and keeps no own user directory.
2. **Every request has a subject and a client.** The subject is a user, an application or an anonymous visitor; the
   client is the front end or integration it arrived through. Both are part of every authorisation decision.
3. **One policy engine** (`IAccessPolicy` in `Ume.Rag.Domain`) used by the Chat/Knowledge API, the MCP server, the
   tool hub and the admin.
4. **Fail closed**: security-relevant configuration is used only at the current config version (platform plan §4).
5. **Least privilege downstream**: e.g. `Sites.Selected` instead of `Sites.Read.All`.
6. **No approval blocks development**: local development, CI and most of P1 run on Keycloak and keys.

## 2. When Entra registrations are actually needed

| Need | Works without an Entra registration? | Needed from |
|---|---|---|
| Development and CI | Yes: Keycloak with the same claims | never |
| Platform → gateway | Yes: gateway virtual key (delegating) | never |
| Open WebUI → Chat API | Yes: gateway-style key (`ume-rag-sk-…`) for the connection + Open WebUI's signed user JWT | never |
| Integrations → our APIs | Yes: keys | only if an app prefers OAuth tokens |
| URL lists, crawling, uploads | Yes | never |
| **Staff sign-in to the admin UI** | Only with Keycloak/local accounts | **pilot** |
| **Pilot users' sign-in to Open WebUI** | Only with local Open WebUI accounts | **pilot** (if municipal accounts are wanted) |
| Group- or department-based assistant audiences for chat users | No | P2 (pilot audience = "users of this client") |
| `rag-api` scopes (own chat client, MCP server, OBO) | No | P2–P3 |
| SharePoint (`rag-ingestion`) | No | P2 |

**Approval packages** (created by the deployment pipelines as infrastructure as code, approved by a person):

- **Package 1, submitted during P0, approved before the pilot**:
  - *Admin sign-in* (**decided**: the easiest route): a change to the **gateway's existing admin registration**, which
    adds:
    - the RAG admin's redirect URIs (`https://<rag admin host>/signin-oidc`, `/signout-callback-oidc`);
    - the `rag-*` app roles (§3);
    - **its own client secret or certificate** for the RAG BFF, so the two systems rotate credentials independently
      and never share a secret;
    - `GroupMember.Read.All` for feature C.

    One registration, one sign-in experience, and the existing user/group assignments carry over. Fallback, only if
    the gateway admin is not on Entra yet or IT refuses the change: a twin `rag-admin` registration configured
    exactly like the gateway's (`identity-and-roles.md`, *Entra ID* steps 1–5).
  - *Open WebUI sign-in*: an `open-webui` registration (its own app).
  - Pilot groups and their assignments.
- **Package 2, submitted during P1, for P2**: `rag-api` (scopes and app roles), `rag-ingestion` (`Sites.Selected`),
  `rag-tools`, `rag-chat-client`, the first municipal MCP server.
- **Later**: OBO downstream consents (P3). Per-site `Sites.Selected` grants are made by admins without the slow
  approval (answered question 2).

On Azure, daemons use managed identities or workload identity federation; on-prem, certificates rather than secrets.
Only tokens from the municipality's own tenant are accepted (`tid` check); guest accounts are refused.

## 3. People in the admin UI (same model as the gateway)

**Mechanism**: the gateway's BFF pattern unchanged: OIDC code + PKCE, secure HttpOnly cookie, CSRF, 8-hour session,
extend before expiry, never more than `Admin:MaxSessionHours` (12) from sign-in; audit actor is the `sub` claim.

**Claims**: exactly the gateway's names and meanings:

| Claim | Meaning (same as the gateway) |
|---|---|
| `roles` | Role values (below) |
| `departmentCodes` | Cost-centre codes of the user's departments; matched against the department's cost-centre code from the gateway |
| `teamCodes` | Team codes, for team-scoped roles (none in the pilot) |
| `name`, `email`, `sub` | Display and audit |

**Role values** are namespaced so they can share one registration with the gateway without ever granting gateway
rights (or the reverse):

| Role value | Meaning in the RAG platform |
|---|---|
| `rag-platform-admin` | Everything (like `gateway-admin`) |
| `rag-department-admin` | Everything in own departments (like `department-admin`) |
| `rag-pipeline-editor` | Canvas editing in own departments |
| `rag-viewer` | Read status, runs, evaluation, usage in own departments (like `viewer`) |
| `rag-editor` (from P2) | Sources, collections, assistants, skills via forms in own departments |

**Configuration keys**: the gateway's OIDC settings with a `UME_RAG_` prefix and the same defaults and semantics:
`UME_RAG_OIDC_AUTHORITY`, `_CLIENT_ID`, `_CLIENT_SECRET` (file, as `init-deployment.sh` does), `_ROLE_CLAIM`
(`roles`), `_DEPARTMENT_CLAIM` (`departmentCodes`), `_TEAM_CLAIM` (`teamCodes`), `_GROUP_CLAIM` (`groups`),
`_GROUPS_<ROLE>` (groups that grant a role, `;`-separated), `_EXTRA_SCOPE`. If both systems use one registration,
authority and client id are the same values in both `.env` files.

**Roles from Entra**: as the gateway recommends, **app roles assigned to groups** (`roles` claim), so no token group
lists and no 200-group overage. Group mapping through `UME_RAG_OIDC_GROUPS_<ROLE>` (object ids) works as an
alternative, exactly as in the gateway.

**Departments without a department attribute.** Both systems get department scope only from `departmentCodes`, and
the municipality has no such attribute today (answered: users are organised in groups). Rather than a RAG-only
mapping, the same small feature is added to **both systems** (see `../gateway-changes/plan.md`, feature C):

- A department (in the gateway's *Organisation* page, and mirrored here because organisation data comes from the
  gateway) may list **member groups** (Entra object ids).
- At sign-in, a user's department scope = codes from the `departmentCodes` claim **plus** departments whose member
  groups the user is in. Their *role* still comes from `roles`.
- Group membership comes from the `groups` claim (groups assigned to the application) or, on overage, a Graph
  `checkMemberGroups` call, cached for 10 minutes. This needs `GroupMember.Read.All` (application) on the admin
  registration, requested in package 1.
- When IT later adds a department attribute, the claim takes over without any code change.

Groups are stored by object id (works for cloud groups now and synced AD groups later, answered question 1); names
are looked up via Graph for display; there is no naming convention, so admins pick groups by search (question 5).

## 4. Callers of the data-plane APIs

| Caller | Credential | Subject | Client (for ceiling) | Phase |
|---|---|---|---|---|
| Open WebUI | Connection key + HS256 JWT forwarded by Open WebUI (`X-OpenWebUI-User-Jwt`) | User (email/`sub` from Open WebUI) | The Open WebUI instance | P1 |
| Integrations | **Keys** created by admins (gateway virtual-key style) or **OAuth client-credentials tokens** (gateway Data API style) | Application | The key or `azp` | P1 keys / P2 tokens |
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

- **P1**: `clients` only: e.g. "everyone who can sign in to the pilot's Open WebUI". Open WebUI's own Entra login
  (with *assignment required* on its enterprise app) decides who that is. No Graph lookup needed.
- **P2**: also `departments` (department scope as in §3) and `groups` (object ids), for callers with real tokens, and
  for Open WebUI users resolved via Graph by email.

**Decision** = subject may use the resource (audience) **and** the resource's classification ≤ the client's ceiling
**and** the subject type is allowed (no anonymous access to `internal`) **and**, for tools, the tool policy allows it.

**Client registry** (review R5): every client has an id, a classification ceiling and a data-handling profile. Pilot
defaults: own chat client `sensitive`, Open WebUI `internal`, widget `public`, integrations as configured.

- **MCP clients: `public` by default**; a platform admin may raise a vetted client to `internal`, never `sensitive`.
- **Clients that keep their own copy of conversations** (Open WebUI, Teams) have a ceiling of at most `internal`.

**Security events** use the gateway's scheme: a `Ume.Rag.Security` log category with stable event names and numbers
(a separate number range from the gateway's), sent over OpenTelemetry, so the same collector rules route both
systems to the SIEM. Every denial is a security event.

## 6. MCP authorisation (platform as MCP server, P2)

- The MCP endpoint is an OAuth **protected resource** publishing Protected Resource Metadata (RFC 9728) pointing at
  Entra; tokens need `aud = rag-api` and scope `Mcp.Use`.
- Entra has no dynamic client registration, so each MCP client is **pre-registered** and added to the client
  registry with a ceiling. Newer registration options (Client ID Metadata Documents) in Entra and MCP clients: to verify.

## 7. Downstream access: service identity now, on-behalf-of later

- **P2, service identity**: the tool hub calls MCP servers with `rag-tools` client credentials or an API key the tool
  server requires. Read-only tools only.
- **P3, on-behalf-of**: the tool hub exchanges the user's `rag-api` token for a downstream token with Entra OBO.
  - Needs a real Entra token: our own client, Teams and MCP clients send one; Open WebUI can too with
    `auth_type: system_oauth` if its login requests `api://rag-api/Chat.Use`
    (`open-webui/backend/open_webui/utils/headers.py:209-212`; to test in P2).
  - **Decided**: the exchange uses the `rag-api` registration's credential (Entra requires the token to be issued to
    the app doing the exchange); `rag-tools` stays for service-identity calls.
  - The user token is an internal, short-lived hand-off from the Chat API, never stored or logged; OBO caches are
    encrypted, per user and short-lived; claims challenges are passed back to the client.
- Non-Entra per-user OAuth for third-party tools: out of scope until a concrete need appears.

## 8. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Keycloak realm with the gateway's claims for dev and CI; OIDC and claim mapping copied from the gateway (`UME_RAG_OIDC_*`); keys in gateway style; package 1 submitted |
| P1 | Admin sign-in on the municipal IdP (shared or twin registration); department scope from groups (feature C, both systems); Open WebUI sign-in and JWT trust; client registry with ceilings; client-based audiences; security events; package 2 submitted |
| P2 | `rag-api` tokens and OAuth machine clients, department/group audiences, MCP server authorisation, `rag-ingestion`, `rag-tools`, `ume-rag-pk-` widget keys |
| P3 | OBO for user-delegated tools, Teams client, claims-challenge handling |

## 9. Answered questions (2026-10-29)

1. **Group type**: start with Entra cloud groups; synced AD groups later work unchanged (object ids).
2. **SharePoint site grants**: made by admins without the slow approval; only `rag-ingestion` needs approval.
3. **Guests**: no; only accounts in the municipality's tenant.
4. **Citizen login**: out of scope for now.
5. **Group naming**: no convention; groups are picked by search.
6. **Approval timing**: no registration is needed for P0 or development; package 1 (admin and Open WebUI sign-in)
   only has to be approved before the pilot.

7. **Admin registration**: add the RAG admin to the gateway's existing admin registration (easiest), with its own
   credential; a twin `rag-admin` registration only as a fallback.
