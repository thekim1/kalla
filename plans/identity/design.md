# Identity, authentication and authorisation: detailed design

Detail step 3 of the Ume.Rag design. Details [`../archive/area-plans/identity.md`](../archive/area-plans/identity.md) (archived: history, not binding; this design states every rule, the plan's answered questions in §15) and the
Decisions table in [`../decisions.md`](../decisions.md). Consistent with
[`../platform/data-model.md`](../platform/data-model.md) (step 1) and
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) (step 2); deviations are listed in §14.
The rules of the weakness review and the simplification pass are stated here (reasoning:
[`../archive/passes/simplification-pass.md`](../archive/passes/simplification-pass.md); its §15 gives assistant versions and owner-team scoping,
§1.3, §4.3); what the pass removed is listed in §17, what the weakness review deferred in §18.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5); Open WebUI citations are in
`~/git/rag-platforms/open-webui`. "Copy" means the file is copied with namespace and names changed only; "adapt" means
copied and changed as stated; "new" means there is no gateway counterpart.

**Identity provider per phase** (`../decisions.md` "P1 pilot sign-in"; plan §1):

| Phase | Provider | RAG's part |
|---|---|---|
| Dev and CI | each repo's own Keycloak in its AppHost, realm `ume` from `dev/keycloak/ume-realm.json`, with test users | §11.1 |
| **P1 (pilot)** | **one Keycloak shared with the LLM gateway**: its own Compose project on the gateway's production host, defined in the gateway repo, database `keycloak` in the gateway's Postgres; one realm `ume` with both systems' clients and roles; local accounts without OTP (MFA returns with brokered Entra ID or AD), LDAP federation to the municipal AD later | a realm-additions file `deploy/keycloak/**` (stream B, P1 item 16 → J8), no test users (§11.2) |
| **P2** | Entra ID, approval package 1 (`infra/entra/`; kept, not yet submitted) | §12 |
| **Production** | AD and Entra ID, both: brokered through Keycloak or directly through `Oidc:*` (Decisions "Identity providers: AD and Entra ID through Keycloak, Ume.Rag standalone later") | §11.2 |

The code path is the same in every phase: OIDC code + PKCE for the admin and Open WebUI, JWT bearer for RAG API
tokens, configured by `Oidc:*`, `RagApi:*` and the Open WebUI OIDC settings. Moving from Keycloak to Entra is
configuration, not code. Where this document describes Entra-only claims (`oid`, `tid`, `acct`, `idp`, `idtyp`,
`groupMembershipClaims`, app-role assignments), that is the **P2** design; the P1 behaviour on Keycloak is stated next
to it.

**What is copied from the gateway**

| Area | Gateway source | Here |
|---|---|---|
| OIDC code + PKCE BFF, server-side cookie session, CSRF, 8 h / 12 h sessions | `src/Ume.LlmGateway.AdminApi/AdminAuthentication.cs:71-233`, `AdminSessions.cs`, ADR 002 | §1 (adapt) |
| Claim mapping (`roles`, `departmentCodes`, `teamCodes`, group → role) | `AdminAuthentication.cs:18-65`, `AdminOptions.cs:107-131` | §1.3 (copy) |
| Department scope from `departmentCodes` matched on cost-centre code | `AdminContext.cs:81-90`, `docs/identity-and-roles.md:26-31` | §1.3, §2 (copy + feature C) |
| Virtual keys: HMAC + pepper, cache incl. negative cache, failure buckets, per-address throttle, 24 h grace, audited reveal | `Domain/Services/VirtualKeyHasher.cs`, `Gateway/Pipeline/KeyAuthenticator.cs`, `AuthFailureRecorder.cs`, `AuthFailureThrottle.cs`, `Domain/Services/KeyRotation.cs`, `AdminApi/KeyEndpoints.cs:79-133` | §6 (adapt) |
| Machine tokens: `Authority`/`Audience`/`PermissionClaims`, keys vs tokens never mixed | `DataApi/DataApiAuthentication.cs`, `DataApiOptions.cs` | §7 (adapt) |
| Security events: one category, stable names and ids, `Printable()`, no secrets | `Infrastructure/Security/SecurityEvents.cs`, `docs/data-access.md:54-133` | §10 (copy pattern) |
| Keycloak dev realm; claim mappers for the shared P1 realm | `dev/keycloak/ume-realm.json`, `docs/identity-and-roles.md:132-158` | §11 (adapt) |

## 1. Admin sign-in (BFF)

### 1.1 Mechanism (unchanged from the gateway)

- Cookie scheme as default, OIDC as challenge scheme (`AdminAuthentication.cs:82-86`); code flow with PKCE,
  `SaveTokens = false`, `MapInboundClaims = false`, name claim `name`, role claim `roles`, scope `email` added
  (`:113-122`). No token ever reaches the browser (ADR 002, `docs/adr.md:6`).
- Claim mapping runs in `OnTokenValidated` (`:123-127`); feature C (§2) and the tenant/guest check (§1.7) run there
  too, right after it.
- Calls to `/api/*` without a session answer 401 instead of redirecting (`:111`, `:130-138`); 403 for missing rights
  (`:112`). Sign-out returns the IdP's end-session URL as JSON (`:139-149`).
- Server-side sessions: the cookie holds only a random id; the ticket is Data-Protection-encrypted
  (`AdminSessions.cs:16-54`) and stored in Postgres, `config."AdminSessions"` (session id, ciphertext, expiry; owned by
  `rag-admin`), through a Postgres implementation of `IAdminSessionStore` (`Infrastructure/Stores/Stores.cs:147-154`;
  the gateway has Redis and in-memory ones, `InfrastructureExtensions.cs:110`, `:120`). A read ignores expired rows;
  the admin's retention step deletes them. The Data Protection key ring is already in Postgres
  (`config."DataProtectionKeys"`, as the gateway's `PersistKeysToDbContext`, `InfrastructureExtensions.cs:60-63`), so
  sign-in needs no Redis (simplification pass §14). The gateway's production check that demands Redis with TLS and a
  restricted ACL user (`:92-100`) applies in our copy only when `ConnectionStrings:redis` is set (P2).
- Lifetime 8 h, no sliding (`AdminAuthentication.cs:93-94`, `AdminSessions.cs:64`); `/bff/session/extend` moves
  expiry by up to 8 h but never past `Admin:MaxSessionHours` (12) from sign-in (`AdminSessions.cs:87-112`,
  `AdminAuthentication.cs:217-232`, `AdminOptions.cs:19-20`). Roles and departments are read at sign-in, so IdP
  changes apply at the latest after 12 h or at the next sign-in (`docs/identity-and-roles.md:79-83`).
- CSRF: antiforgery header `X-XSRF-TOKEN`, a readable token cookie set by `/bff/user`, and `X-Requested-With:
  XMLHttpRequest` required on every non-GET to `/api` and `/bff` (`AdminAuthentication.cs:74-81`, `:190-194`,
  `:281-305`).
- A random session id claim `ume_session` is added at first sign-in only (not on extension) and goes into audit
  entries and security events (`:100-107`, `:255-256`).
- Audit actor = **`oid`**, else `sub`, else name (adapted from `AdminContext.cs:76-79`, which uses `sub`; `oid` is the
  same user id in every registration of the tenant, §1.6); actor context (name, roles, departments, address,
  session, request id, user agent) from `AdminActor` (`AdminActivity.cs:18-57`). Built as
  `AdminActor.SubjectOf` (`src/Ume.Rag.AdminApi/AdminActor.cs:38-40`), used by `AdminContext.Actor`
  (`src/Ume.Rag.AdminApi/AdminContext.cs:54-55`). **P1 (Keycloak):** Keycloak issues no `oid`, so the actor is `sub`, the Keycloak user id;
  it is the same in every client of realm `ume`, so it equals the gateway's actor (`sub`) for the same person. It changes
  when the provider changes (P2 Entra `oid`; possibly with LDAP federation, §16), so one person can appear under two
  actor ids in the audit log.

### 1.2 Class map

| Gateway (`Ume.LlmGateway.AdminApi`) | Ume.Rag (`Ume.Rag.AdminApi`) | How |
|---|---|---|
| `AdminAuthentication.ApplyClaimMapping`, `CopyClaims` (`AdminAuthentication.cs:18-65`) | same | copy |
| `AdminAuthentication.AddAdminAuthentication` (`:71-174`) | same | adapt: cookie names (below), policies for `rag-*` roles, `OnTokenValidated` adds §1.7 and §2.3 |
| `AdminAuthentication.MapAdminAuthentication` (`:176-233`) | same | adapt: `/bff/user` adds `departmentSources` (§1.5) |
| `AdminRoles` (`:237-245`) | `RagAdminRoles` | adapt: values in §1.3 |
| `AdminClaims` (`:248-257`) | same | adapt: adds `oid` (`sub`, `roles`, `departmentCodes`, `teamCodes`, `ume_session`) |
| `AdminPolicies` (`:260-279`) | same | adapt: §1.3 |
| `AdminAntiforgeryMiddleware` (`:281-305`) | same | copy |
| `OidcOptions` (`AdminOptions.cs:107-131`) | same | adapt: default `ClientId` `ume-rag-admin`; new `TenantId`, `RefuseGuests` (§1.4) |
| `AdminOptions` (`AdminOptions.cs:7-51`) | same | adapt: keep `RequestsPerMinute`, `MaxSessionHours`, `AuditSourceAddress`; drop price/release settings |
| `AdminTicketStore`, `AdminSessionLimit` (`AdminSessions.cs`) | same | copy; protector purpose `Ume.Rag.AdminApi.Session.v1`; sign-in-time item `.ume-rag.signedIn` (the gateway's is `.ume.signedIn`), so a ticket from one system never reads as the other's |
| `IAdminSessionStore` (`Infrastructure/Stores/Stores.cs:147-154`), `RedisAdminSessionStore`, `InMemoryAdminSessionStore` | `IAdminSessionStore`, `PostgresAdminSessionStore` | adapt: Postgres implementation over `config."AdminSessions"` (§1.1); the Redis one is not copied |
| `AdminActor`, `AdminActivityMiddleware`, `AccessDeniedThrottle`, `AdminClientAddresses` (`AdminActivity.cs`) | same | copy; `AdminClientAddresses` reads `Settings.TrustedProxies` (the `network` settings section, admin design §4.5) at most every 30 s, so `SourceAddress` is the client's address behind a trusted proxy and the connection address otherwise (`docs/security-events.md`) |
| `AdminContext` scope methods (`AdminContext.cs:69-130`) | `AdminContext` | adapt: `IsAdmin` = `rag-platform-admin`; `Departments()` unchanged; teams/keys per RAG roles; actor `oid` (§1.1) |
| – | `DepartmentMembershipResolver` | new (§2) |

Cookie names must differ from the gateway's: browsers ignore the port, so on `localhost` the gateway admin (5173)
and ours (5174) would overwrite each other's `__Host-ume-admin` and `XSRF-TOKEN`. Ours: `__Host-ume-rag-admin`
(session), `__Host-ume-rag-csrf` (antiforgery), `RAG-XSRF-TOKEN` (readable token; the copied admin UI's fetch helper
reads this name).

### 1.3 Claim mapping and role resolution

`ApplyClaimMapping` is copied unchanged: it expands JSON-array claims for the role, department, team and group claims
(`AdminAuthentication.cs:24-36`), copies differently named claims to `roles`/`departmentCodes`/`teamCodes`
(`:37-39`, `:51-65`) and adds a role for each role whose groups intersect the group claim, case-insensitively (`:40-48`). The groups come
from `Settings.Identity.RoleGroups` (edited under *Settings → Role groups*, read at each sign-in, so a change applies
at the next sign-in on every instance) merged with the deployment's `Oidc:RoleGroups` (`;`-separated), which only
adds: `Oidc:RoleGroups:rag-platform-admin` is the break-glass and the setting can't remove it (Decisions "Role groups
as a setting"; M-17 C10, #693). A removed mapping takes effect at the user's next sign-in, at the latest when the
admin session's fixed lifetime ends; to revoke at once, also end the user's admin sessions.

| Role value | Policy membership | Scope |
|---|---|---|
| `rag-platform-admin` | `Admin`, `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read` (neither `Review` nor `FeedbackComments`) | every department (like `gateway-admin`, `AdminContext.cs:81-86`) |
| `rag-department-admin` | `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read`, `FeedbackComments` | `departmentCodes` (+ feature C) |
| `rag-pipeline-editor` | `EditPipelines`, `Read` | same |
| `rag-reviewer` (P1) | `Review`, `FeedbackComments`, `Read` | same (feedback comments and conversations with a reason, audited; admin design §3.1, §8). Nothing to read while the pilot runs history mode `none` (weakness review §4) |
| `rag-skill-editor` (P2) | `EditSkills`, `Read` | same (draft, publish, serve, roll back skills; tools design §3.6) |
| `rag-editor` (P2) | `Edit`, `Read` | same; links published skills to assistants, cannot edit skills (tools design §3.6) |
| `rag-viewer` | `Read` | same |
| none | nothing: every `/api` call is 403 (the gateway allows the catalogue to role-less users, `docs/identity-and-roles.md:18`; we have no public page) | – |

As in the gateway, policies are `RequireRole(any of …)` (`AdminAuthentication.cs:151-158`), so several roles add up
and the highest one counts (`docs/identity-and-roles.md:22-24`). Role values that are not `rag-*` are ignored. `Review` (open a conversation) and `FeedbackComments` (reveal one
feedback comment) are not implied by `Admin`: no administrative role grants conversation content, so a platform admin
who must review is also given `rag-reviewer`. `teamCodes` grants nothing in P1 (no team-scoped role).

**Owner-team scoping (P2, simplification pass §15).** Assistants, collections and skills keep an owner team; a
department may switch on `DepartmentSettings.EditOwnerTeamOnly` (data-model §2.1). Then an `Edit`, `EditPipelines` or
`EditSkills` write to such an object also needs the object's owner team among the user's teams; holders of `Manage` in
the department (department and platform admins) are unaffected, and reads never change. The user's teams are the
`teamCodes` claim values matched case-insensitively to `Teams.Code` from the gateway mirror, so no new claim or
configuration key is needed. It is an admin authorisation rule (`AdminContext.CanWrite`, admin design §3.2), not a
role and not an `IAccessPolicy` step: the data plane never asks who may edit. A refused write is 403 `owner_team_only`
with event 6006; there is no new security event. Whether the municipality can fill `teamCodes` at all is to verify
(§16); without it, member groups on teams would follow the feature C pattern in both systems.

### 1.4 Settings

Same names and semantics as the gateway (`docs/identity-and-roles.md:106-121`, `deploy/compose.prod.yaml:136-146`)
with the `UME_RAG_` prefix; for local runs the same keys as `Oidc:<Setting>` in AppHost user secrets.

| Env (`deploy/.env`) | Setting | Default | Notes |
|---|---|---|---|
| `UME_RAG_OIDC_AUTHORITY` | `Oidc:Authority` | – (required) | P1: the shared Keycloak, `https://<keycloak host>/realms/ume` (the gateway's form, `docs/identity-and-roles.md:156`); P2 Entra: `https://login.microsoftonline.com/<tid>/v2.0` |
| `UME_RAG_OIDC_CLIENT_ID` | `Oidc:ClientId` | `ume-rag-admin` | P1: the Keycloak client id `ume-rag-admin` (the default); P2 Entra: the `ume-rag-admin` application id (`init-deployment.sh --oidc-client-id`, repo-and-deployment §7.1) |
| (secret file) `Oidc__ClientSecret` | `Oidc:ClientSecret` | – | P1: the `ume-rag-admin` client's secret in the shared realm (§11.2); P2: the registration's own secret (§12.2); `init-deployment.sh --oidc-client-secret-file` as in the gateway |
| `UME_RAG_OIDC_ROLE_CLAIM` | `Oidc:RoleClaim` | `roles` | |
| `UME_RAG_OIDC_DEPARTMENT_CLAIM` | `Oidc:DepartmentClaim` | `departmentCodes` | |
| `UME_RAG_OIDC_TEAM_CLAIM` | `Oidc:TeamClaim` | `teamCodes` | |
| `UME_RAG_OIDC_GROUP_CLAIM` | `Oidc:GroupClaim` | `groups` | |
| `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` | `Oidc:RoleGroups:rag-platform-admin` | – | the break-glass for the first platform admin: `;`-separated, case-insensitive; every group is a `rag-` name or an Entra object id, else the admin API refuses to start (§2.5) |
| *(no variable)* | `Settings.Identity.RoleGroups` (database, *Settings → Role groups*) | – | the groups of `rag-platform-admin`, `rag-department-admin`, `rag-pipeline-editor`, `rag-reviewer`, `rag-editor` and `rag-viewer` (`rag-skill-editor` from P2); the same `rag-`-or-object-id rule, refused by the admin API on save; merged with the row above (M-17 C10) |
| `UME_RAG_OIDC_EXTRA_SCOPE` | `Oidc:ExtraScopes:0` | `email` | |
| `UME_RAG_OIDC_TENANT_ID` | `Oidc:TenantId` | – | **new**: when set, `tid` must equal it (§1.7). **Unset in P1**: Keycloak tokens carry no `tid`, so a set value refuses every sign-in (`src/Ume.Rag.AdminApi/TenantCheck.cs:19-23`); set with Entra in P2 |
| `UME_RAG_OIDC_REFUSE_GUESTS` | `Oidc:RefuseGuests` | `true` | **new** (§1.7); harmless on Keycloak (no-op) |
| – | `Admin:MaxSessionHours` | 12 | copy (`AdminOptions.cs:19-20`) |
| – | `Admin:RequestsPerMinute` | 120 | copy (`AdminOptions.cs:12-13`) |
| – | `Admin:AuditSourceAddress` | `Full` | copy (`AdminOptions.cs:26`) |

### 1.5 `/bff/user`

Copied (`AdminAuthentication.cs:188-208`): `isAuthenticated`, `environment`, `name`, `email`, `roles`,
`departmentCodes`, `teamCodes`, `sessionExpiresAt`, `sessionLimitAt`. One addition, also in the gateway (feature C,
§2.7): `departmentSources` (`[{ code, source: "claim" | "group" }]`), so the setup check in
`docs/identity-and-roles.md:217-229` also shows where a department came from. No group ids are returned. Also
`organisationName`, sent before sign-in too: the gateway context's `gateway.organisation_name` under the feature
`organisation-name` (gateway v0.10.2; M-17 C12), trimmed; null when empty, without the feature or before the first
context answer.

### 1.6 Own registration (decided)

Our BFF uses its **own** client `ume-rag-admin` (plan §2, answered question 7, revised 2026-10-30), next to the
gateway's `ume-admin`, with its own client secret. The code is the gateway's; only `UME_RAG_OIDC_CLIENT_ID` and the
client definition differ. Rotating or revoking either system's credential never affects the other, and a user with
only `rag-*` roles gets nothing in the gateway admin (the gateway's roles are `gateway-admin`, `department-admin`,
`viewer`, `team-admin` and Data API permissions such as `catalog.read`, gateway `dev/keycloak/ume-realm.json:9-18`;
ours are `rag-*` plus `Knowledge.Read.All` and `Chat.Invoke.All`, so the names never collide).

- **P1, shared Keycloak:** `ume-rag-admin` is a confidential client in realm `ume`, configured like the gateway's
  Keycloak client (`docs/identity-and-roles.md:137-153`) and defined in RAG's realm additions (§11.2). Roles are realm
  roles `rag-*` given to users or groups; with *full scope allowed* off on every client, a token of `ume-rag-admin`
  carries only `rag-*` roles and a token of `ume-admin` only the gateway's. Keycloak's `sub` is the user id, the same in
  every client, so the audit actor is `sub` (§1.1).
- **P2, Entra:** the registration `ume-rag-admin`, configured like the gateway's (`docs/identity-and-roles.md:160-182`)
  plus §12's additions, with its own client secret (§12.2). Users get `rag-*` app roles on it; the gateway's
  registration and its assignments are untouched. Because `sub` is pairwise per application in Entra, the audit actor
  is `oid` (§1.1).

Development and CI use the same model (dev Keycloak client `ume-rag-admin`, §11.1), so both stacks run side by side.

### 1.7 Tenant and guests (new, both systems)

Run in `OnTokenValidated` before claim mapping; a refusal ends the OIDC callback with a redirect to
`/?signin=refused&reason=<reason>`, where the admin UI explains it (no session; same in both systems, gateway design
§4.6) and event 5001 (`Reason`
`wrong_tenant` / `guest_refused`). **One rule in both systems:**

- `Oidc:TenantId` set ⇒ `tid` must equal it (a missing `tid` is refused too). (The v2 issuer already contains the
  tenant; this also covers a misconfigured `common` authority.)
- `Oidc:RefuseGuests` (default `true`) ⇒ refuse when the optional claim `acct` is `1` (guest), or when an `idp` claim
  is present and differs from both the issuer and `https://sts.windows.net/{tid}/` (Entra sets `idp` for users from
  another tenant or IdP). `acct` is requested as an optional ID-token claim on the registration (§12.3). Keycloak and
  AD FS emit neither claim, so the check is a no-op there.

The same two checks guard RAG API tokens (§7.1) and, in the gateway, admin sign-in and Data API tokens (gateway
release 2, P2, gateway design §4.6; weakness review §5).

**P1 (shared Keycloak): both checks are no-ops.** Keycloak emits none of `tid`, `acct`, `idp`, so `Oidc:TenantId` and
`RagApi:TenantId` stay unset (a set value would refuse every Keycloak user, `src/Ume.Rag.AdminApi/TenantCheck.cs:19-23`) and
`Oidc:RefuseGuests` finds nothing to refuse (`src/Ume.Rag.AdminApi/TenantCheck.cs:30-41`). The code (`TenantCheck.cs`,
called before claim mapping at `src/Ume.Rag.AdminApi/AdminAuthentication.cs:83`) stays and takes effect with Entra in P2. What the tenant boundary does
on Entra is done in P1 by the realm: only accounts in realm `ume` can sign in, and there are no guest or brokered
accounts (local accounts only; LDAP federation later adds no brokered identity provider). The realm is shared, so a
gateway-only account can complete sign-in to our admin: it has no `rag-*` role, so every `/api` call is 403 (§1.3). Who
may sign in to Open WebUI is open (§5.3, §16).

## 2. Feature C: department scope from member groups (both systems, claims only)

**Phase:** the gateway delivers member groups with **v0.10.0** (#580, thekim1/llmgateway#85, 2026-11-12): each
department of `GET /v1/ume/context` carries `member_groups` under the scope `organisation.read` (feature
`member-groups`). Department scope comes from the `departmentCodes` claim **plus the mirrored member groups**, matched
against the `groups` claim by the resolver (§2.3). The user chose to wait for the gateway rather than build another
source (2026-11-12), so the interim hand entry on Ume.Rag's department record goes with the sync that reads them
(B-35). On the shared Keycloak both are set on the account: `departmentCodes` is a user attribute shared with the
gateway (one value scopes the user in both systems), and a member group is a Keycloak group name (§2.2, §2.5).

### 2.1 Where member groups live

- **Gateway (source of truth)**: `Department` (`Domain/Entities/Entities.cs:8-19`) gains `MemberGroups`
  (`varchar(256)[]`, default empty), edited by gateway admins on the *Organisation* page; every change is an audit
  entry (`admin.change`).
- **Gateway context**: each department in `GET /v1/ume/context` carries `member_groups` under the scope
  `organisation.read` (feature `member-groups`, gateway v0.10.0; gateway-changes §5.5).
- **Ume.Rag (mirror)**: `config."Departments"."MemberGroups"` (data-model §2.1, no GIN index: never queried by group).
  The admin API's `OrgSync` job reads departments and teams from `GET /v1/ume/context` every **5 minutes** and on
  demand (*Sync now* on our organisation page), with `rag-admin`'s credential key, which holds the scope
  `organisation.read` (gateway design §5.5, admin design §4.10); there is no Data API key. The credential key needs no
  IdP registration, which keeps the "no registration before the pilot" rule (plan §2). Only changed rows are written,
  so an unchanged sync does not bump the config version (data-model §2.1). The sync replaces `MemberGroups` from
  `member_groups` when `gateway.features` contains `member-groups`; they are never edited in our admin UI, which shows
  them read-only and links to the gateway's *Organisation* page.

**Freshness**: a change in the gateway reaches our mirror within 5 minutes (or at once with *Sync now*), and a
signed-in admin's scope at their next sign-in (scopes are fixed at sign-in, §1.1). If the sync fails, the last mirror
stays in use (it is never wider than the gateway was at that time); after 3 failed runs a warning shows on the
*Gateway* page and in health (`degraded`).

### 2.2 Group values: claim values, not only object ids (decision)

The plan stores groups "by object id" for Entra. The gateway is open source and also documents Keycloak and AD FS,
where the group claim holds **names** (`docs/identity-and-roles.md:116`, `:146-147`, `:199-215`), and its existing
`Oidc:RoleGroups` already matches claim values case-insensitively (`AdminAuthentication.cs:40-48`). So a member group
is **a value of the group claim**: an Entra object id, an AD FS/Keycloak group name. Matching is case-insensitive
like `RoleGroups`. On Entra (P2) this is exactly the plan's "object id" rule; on Keycloak (dev and the P1 shared
realm) it is a group name from a plain group mapper (repo-and-deployment §4.2, §11.2), so in P1 the hand-entered
member groups (§2) are Keycloak group names, not object ids.

### 2.3 Sign-in resolution order

In `OnTokenValidated`, after `ApplyClaimMapping` (identical in both systems; `DepartmentMembershipResolver`):

1. Codes from the `departmentCodes` claim (after mapping) → `departmentSources` `claim`.
2. `candidates` = member groups of every **active** department in the mirror (gateway: its own table) whose
   cost-centre code is not already in step 1.
3. `known` = the group claim values (`Oidc:GroupClaim`) ∩ `candidates`.
4. For each department with at least one member group in `known`: add a `departmentCodes` claim with its cost-centre
   code → source `group`.

Because the result is written as ordinary `departmentCodes` claims, every scoped query (`AdminContext.Departments()`,
`AdminContext.cs:81-90`) works unchanged in both systems, and `/bff/user` shows the result. Roles still come only from
`roles` or `Oidc:RoleGroups` (plan §3): membership never grants a role. Only the token is read: a member group the
token does not show adds nothing.

### 2.4 Graph lookup (Entra)

**Later (P2):** the Graph `checkMemberGroups` lookup comes with package 2 (reasoning: simplification pass §7; §17).

### 2.5 Group claim per IdP

- **Entra (P2)**: `ume-rag-admin` sets `groupMembershipClaims = SecurityGroup` (§12.3), so the ID token's `groups` claim
  carries the object ids of the user's security groups, cloud and synced, without assigning department groups to the
  application (assigning them would let whole departments sign in). Roles still come from app roles. Above the token's
  group limit Entra omits `groups` and sets `_claim_names.groups` (overage); that user then gets claim codes only
  (§2.6). We never follow `_claim_sources`.
- **Keycloak (dev, P1)**: a *Group Membership* mapper on `ume-rag-admin` (claim `groups`, *Full group path* off, *Add
  to ID token* on, `docs/identity-and-roles.md:145-147`; dev realm `dev/keycloak/ume-realm.json:765-842`) and member groups entered
  as names. In the shared realm the claim lists every group of the account, the gateway's included; only names
  entered as member groups count (§2.3 step 3). No overage limit applies. Because a role group named like a gateway
  group would grant an Ume.Rag role by accident, every role group (`Oidc:RoleGroups` and `Settings.Identity.RoleGroups`) is a `rag-` name (case-insensitive,
  longer than the prefix) or an Entra object id (a GUID); `OidcOptionsValidator` (`src/Ume.Rag.AdminApi/AdminOptions.cs`)
  refuses to start otherwise, and `PUT /api/settings/identity` refuses to save one, and the gateway gives none of its groups a `rag-` name (`deploy/keycloak/README.md`,
  *Group names*). Member groups are not checked: they give department scope only, never a role, and are the
  gateway's groups on purpose (§2.1). AD FS role groups need `rag-` names too.
- **AD FS**: group claim `group` with unqualified names (`:199-215`).

### 2.6 Failure behaviour (decision: claims only, never fail open)

- No group claim, or an overage token: sign-in succeeds with step 1 codes only; `/bff/user` shows only `claim`
  sources, so the narrowing is visible.
- Mirror sync failing: the last mirror is used (§2.1).

Claims only can only **narrow** a user's scope compared to the gateway's configuration, never widen it, and platform
admins (who need no departments) are never locked out.

### 2.7 Gateway work items for feature C (detailed in `../gateway-changes/design.md` §4)

1. Migration: `Departments.MemberGroups varchar(256)[] NOT NULL DEFAULT '{}'` (no index: never queried by group).
2. Admin API/UI *Organisation*: edit member groups (gateway admins; department admins read them), free text; audit
   `Update Department`.
3. `OidcOptions`: `TenantId`, `RefuseGuests` (§1.4, §1.7). `DepartmentMembershipResolver` in `AdminApi`, called from
   `OnTokenValidated` after `ApplyClaimMapping` (`AdminAuthentication.cs:123-127`).
4. `/bff/user`: `departmentSources`. New events 2008 `admin.signin.refused` and Data API 3003 `data.token.refused`
   (tenant/guest refusals).
5. Integration context: `member_groups` on each department of `GET /v1/ume/context` under `organisation.read` (teams
   already carry `code`). Built in v0.10.0 (thekim1/llmgateway#85).
6. `UME_OIDC_TENANT_ID`, `UME_OIDC_REFUSE_GUESTS` in `compose.prod.yaml` and the Portainer templates.
7. Docs: `identity-and-roles.md` (Entra step 4 alternative "member groups in the group claim"), runbook *Identity
   provider*.
8. Tests (§2.8), shared in spirit with ours.

The two implementations are deliberately the same code shape (`DepartmentMembershipResolver` takes the departments,
the principal and the options), so the class can be copied either way.

### 2.8 Tests (both systems)

| Test | Kind |
|---|---|
| Claim codes and group codes are merged without duplicates; sources reported correctly | unit |
| Group matching is case-insensitive; unmapped groups add nothing; inactive departments add nothing | unit |
| Membership never adds a role | unit |
| Overage token (`_claim_names.groups`, no `groups`) → claim codes only, no group codes | unit |
| Keycloak user `rag-group-member` (no attribute, group `Byggnadsnämnden`) sees department 3000 in `/bff/user` | integration (Keycloak container) |

## 3. Principal model

Every data-plane request becomes one `RagPrincipal` (in `Ume.Rag.Domain`, built by `Ume.Rag.Api` authentication
handlers; the admin API keeps the gateway's `ClaimsPrincipal` + `AdminContext` model).

```csharp
public enum SubjectKind { EntraUser, OwuiUser, Application, Anonymous }   // stored as entra-user, owui-user, application, anonymous
public enum CredentialKind { SecretKey, PublicKey, AccessToken, ServiceKey, DownloadGrant }

public sealed record RagPrincipal(
    SubjectKind Kind,
    string SubjectId,              // stable id, see table; never an email
    string? DisplayName,           // display only, never logged
    string? Email,                 // display only, never logged or stored
    ClientEntry Client,            // from the registry snapshot (§4.1)
    CredentialKind Credential,
    KeyEntry? Key,                 // key id, prefix, team, scopes (keys only)
    IReadOnlySet<string> Permissions,          // Chat.Use, Knowledge.Read, Mcp.Use, *.All (§7.2)
    MembershipFacts Membership,    // departments/groups from token claims and whether they are known (§4.3)
    string? EntraObjectId, string? TenantId,
    long ConfigVersion);
```

| Caller (plan §4) | Credential checked | `Kind` | `SubjectId` | `Client` resolved by | Phase |
|---|---|---|---|---|---|
| Open WebUI | `ume-rag-sk-` connection key **and** `X-OpenWebUI-User-Jwt` (§5) | `OwuiUser` | JWT `sub` (Open WebUI user id) | the key's `ClientId` (kind `open-webui`) | P1 |
| Open WebUI with `system_oauth` (E1) | key in `x-api-key` + Entra token (§9.4) | `EntraUser` | `oid` | the key's `ClientId` | P2 test |
| Integration with a key | `ume-rag-sk-` key | `Application` | `key:{keyId}` | the key's `ClientId` (kind `integration`) | P1 |
| Integration with a token | client-credentials token (§7) | `Application` | `app:{azp}` | `Clients.OAuthClientId = azp` | P2 |
| Public widget | `ume-rag-pk-` key + `Origin` + issued session (§6.5) | `Anonymous` | `anon:{HMAC(session)}` | the key's `ClientId` (kind `widget`) | P2 |
| Own chat client, Teams | delegated token, `scp` `Chat.Use` (§7) | `EntraUser` | `oid` (Keycloak: `sub`) | `azp` | P2/P3 |
| MCP client (`/mcp` and `/mcp/skills/<slug>`, same principal) | delegated token, `scp` `Mcp.Use` (§8) | `EntraUser` | `oid` | `azp` (kind `mcp-client`) | P2 |
| Worker → API internal attachment route (`POST /internal/attachments/{id}/chunks`, chat design §15.2) | internal service key, internal network only | `Application` | `svc:{keyId}` | client `rag-internal` (kind `internal`) | P2 |
| Chat role → knowledge role over HTTP (`Retrieval:Mode=Http`) | internal `ume-rag-sk-` service key | `Application` + forwarded decision | `svc:{keyId}` | client `rag-internal` (kind `internal`) | when split |
| Chat and `mcp` roles → `rag-tools` service (P2: in-process hub, no hop; tools design §2) | **mTLS** client certificate **and** internal `ume-rag-sk-` service key | `Application` + forwarded `ToolCallContext` | `svc:{keyId}` | client `rag-internal` (kind `internal`) | P3 |
| Admin → `rag-tools` service (discovery, connection test; P2 in-process) | mTLS **and** service key | `Application` | `svc:{keyId}` | client `admin-console` (kind `internal`) | P3 |
| Admin test panel | admin session (in-process, no HTTP) | `EntraUser` | admin `oid` (fallback `sub`; P1 Keycloak: `sub`) | client `admin-console` (kind `internal`) | P1 |
| Browser following a citation link (`GET /v1/documents/{id}` and `/content` only) | signed download grant `?grant=g1.…` (knowledge design §4.6) | none (no subject; the grant carries ids only) | – | the grant's `cl` (client that received the citation) | P1 |

Rules:

- `SubjectId` is what conversations (`chat."Conversations"."SubjectId"`, data-model §2.3), per-user rate limits
  (`ume-rag:rl:user:{subjectHash}:{minute}`) and `ToolCallEvents` use. It is pseudonymous but personal data, so security
  events carry it only where the gateway carries an actor (§10).
- When the key itself is the subject (`Application`, e.g. one shared `integration` key behind an app's backend), the
  chat message limit is that key's `Keys.RequestsPerMinute`, else the platform's per-user value; a user subject (Open
  WebUI JWT, token) always gets the per-user value (chat design §4.1 step 4; Decisions "Chat rate limit per key"). The
  P2 widget's per-key limit (§6.5) reads the same field.
- Conversations are unique per (`ClientId`, `SubjectId`, `ExternalChatId`, `AssistantId`) (data-model §2.3, chat
  design §11.1), so an Open WebUI user id never meets an Entra `oid` in one namespace, and two Open WebUI instances
  never share conversations. Issued conversation ids of other clients are signed (`c1.…`, HMAC over client, subject
  and id); a foreign or forged id is a 404 with 5002 reason `conversation_not_owned` (§10).
- `EntraUser` uses `oid` when present, else `sub` (Keycloak and AD FS; both are stable per user). The kind stands for
  a user of the configured OIDC provider whatever it is, so in P1 (shared Keycloak; only the admin test panel uses it)
  its `SubjectId` is the Keycloak `sub`; the name and its stored value `entra-user` are kept.
- The internal service key never carries end-user identity downstream: the chat role has already made the decision
  for the assistant, and the knowledge role checks only "client `rag-internal` may retrieve the collections of
  assistant X at level ≤ X's level" (§4.8).

## 4. Client registry and `IAccessPolicy`

### 4.1 Registry (`config."Clients"`, data-model §2.1)

| Field | Use | Change |
|---|---|---|
| `Id`, `Name`, `IsEnabled` | identity of the front end/integration | – |
| `Kind` | `open-webui`, `widget`, `integration`, `own-client`, `teams`, `mcp-client`, `entra-app`, **`internal`** | `internal` added (§14) |
| `OAuthClientId varchar(200)` | `azp`/`appid`/`client_id` for token callers | replaces `EntraAppId uuid` (Keycloak client ids are strings) (§14) |
| `ClassificationCeiling` | caps every resource | – |
| `StoresHistory`, `ClientRetentionDays` | data-handling profile (R5) | – |
| `AllowedOrigins` | widget | – |
| `AllowedSubjectKinds` (derived from `Kind`, not stored) | §4.3 step 3 | – |
| `ToolPolicy varchar(10)` | P2: client layer of the tool policy, `none`/`read` (P3 `confirm`); defaults `read` for `open-webui`, `own-client`, `teams`, `widget`, `none` for `integration`, `entra-app`, `mcp-client`, `internal` (tools design §5.1) | new (tools design §13) |

Database checks stay as designed: `StoresHistory ⇒ ceiling ≠ sensitive`, `mcp-client ⇒ ceiling ≠ sensitive`.
Pilot rows. The platform's own clients are seeded by the migration service in every deployment, insert-if-absent and
marked managed by the deployment (Decisions "Deploy configuration"; #712): `admin-console`, the deployment's Open WebUI
client while the `openwebui` profile is on (§5.2), and `rag-internal` once P2 uses it. A managed client is unique per
(kind, name) and found by them, so its name is not editable (user, 2026-11-15). A platform admin creates the others:

| Client | Kind | Ceiling | StoresHistory |
|---|---|---|---|
| Open WebUI pilot | `open-webui` | `internal` (only for EU providers under a processor agreement listed in the DPIA, G1; weakness review §4) | yes (90 days; its chat copies unpruned until W2, decided within G1) |
| `admin-console` | `internal` | `sensitive` | no |
| `rag-internal` | `internal` | `sensitive` | no |
| widget (P2) | `widget` | `public` | no |
| own chat client (P2) | `own-client` | `sensitive` | no |
| MCP clients (P2) | `mcp-client` | `public` (a platform admin may raise to `internal`) | n/a (treated as yes) |

**Pilot client and the DPIA** (weakness review §4): if the DPIA (G1) does not list a processor agreement (PUB-avtal)
for the EU providers, the Open WebUI pilot client is limited to on-prem models. **Form (settled):** by model choice,
not by the registry: the client keeps ceiling `internal` (the registry has no on-prem ceiling, and `StoresHistory ⇒
ceiling ≠ sensitive` rules out `sensitive`), and the platform team sets the answer and condense models of every
assistant offered to it to on-prem gateway models without EU fallbacks, which the `internal` residency already
allows. No new registry field. While there is no on-prem model hardware (register M2) this fallback does not exist:
the pilot runs EU-hosted models through the gateway, and if the DPIA refuses EU processing the pilot waits for
hardware or another provider (accepted risk, Decisions table "P1a plan answers").

### 4.2 Interface

`IAccessPolicy` lives in `Ume.Rag.Domain` and is **pure**: no I/O, no clock reads beyond an injected `now`, so the
policy matrix is a table-driven unit test.

```csharp
public interface IAccessPolicy
{
    AccessDecision Decide(RagPrincipal who, ResourceRef what, RagAction action, PolicySnapshot config, bool confirmed);
    IReadOnlyList<ResourceRef> Filter(RagPrincipal who, IEnumerable<ResourceRef> candidates, RagAction action,
        PolicySnapshot config, bool confirmed);
}

public enum RagAction { ListModels, Chat, Search, ReadDocument, McpCall, ToolCall, Feedback }

public sealed record AccessDecision(
    bool Allowed,
    DenyReason Reason,                 // None when allowed
    int HttpStatus,                    // 200/401/403/404/503
    Classification EffectiveLevel,     // the resource's level (drives residency)
    Residency MaxResidency,            // always sent; public = External (§4.7)
    long ConfigVersion,
    string Step);                      // the first failing §4.3 step's id (explain access, §4.9)
```

`PolicySnapshot` is the config snapshot (data-model §5), immutable per version, with `Version`, clients, keys'
scopes, assistants with audiences and their
**published** version's collections (the api reads only `config."PublishedAssistantVersions"`, so a draft never
reaches a decision),
collections, skills, tools, data-protection policies and the department mirror. `confirmed` (step 0's input: the
snapshot was last confirmed at most `Config:MaxUnconfirmedSeconds` ago, default 90 s) is passed with each call, not
kept in the snapshot. Membership comes from the token's
claims as `MembershipFacts { Departments, Groups, Known: bool }` (`Known = false` for an overage token), so the policy
itself never waits.

### 4.3 Evaluation order (first failing step decides)

| # | Check | Deny reason | Status |
|---|---|---|---|
| 0 | Snapshot confirmed within `Config:MaxUnconfirmedSeconds` (default 90 s; data-model §5) | `config_unconfirmed` | 503 |
| 1 | Client exists and `IsEnabled`; the key's client is the client | `client_disabled` / `client_unknown` | 403 |
| 1a | Knowledge routes only: client kind in `Knowledge:OpenToClientKinds` (P1 `internal`; knowledge design §2.3); download grants are checked by their own re-check instead (knowledge design §4.6) | `client_not_allowed` | 403 |
| 2 | Credential kind allowed for the client kind (`open-webui`: sk key + JWT; `widget`: pk key; `integration`: sk key or app token; `own-client`/`teams`/`mcp-client`: delegated token; `internal`: service key or in-process) | `credential_not_allowed` | 403 |
| 3 | Subject kind allowed for the client kind (`widget` ⇒ `Anonymous` only; `Anonymous` only via `widget`) | `subject_not_allowed` | 403 |
| 4 | Permission for the action: delegated `scp` (`Chat.Use` for `ListModels`/`Chat`/`Feedback`, `Knowledge.Read` for `Search`/`ReadDocument`, `Mcp.Use` for `McpCall`), app `roles` (`Chat.Invoke.All`, `Knowledge.Read.All`), keys: key kind (`pk` ⇒ `ListModels`/`Chat`/`Feedback` only) | `permission_missing` | 403 |
| 5 | Resource exists and is live (assistant `State = active` with a published version, collection not deleted) | `not_found` | 404 |
| 6 | Key scope: if `Keys.Scopes` lists assistants/collections, the resource is in it; for an assistant, collection scopes must reach at least one of its published version's collections, and the search is then narrowed to them (knowledge design §4.2) | `key_scope` | 404 |
| 7 | **Classification ceiling**: resource level ≤ client ceiling | `ceiling_exceeded` (event 5005) | 404 |
| 8 | **Anonymous**: resource level = `public` | `anonymous_not_allowed` | 404 |
| 9 | **Data handling**: level policy `ClientsWithoutHistoryOnly` ⇒ `!client.StoresHistory`; `AllowedHistoryModes` contains the assistant's mode; a level whose `DataProtectionPolicies` row was never saved gets the code defaults (`DataProtectionPolicy.DefaultFor(level)`, the same as the admin's), and a level missing from the snapshot fails closed | `client_stores_history` | 404 |
| 10 | **Audience** (assistants; collections via the assistant, or via `CollectionAudiences` for token callers in P2, knowledge design §4.2): any `AssistantAudiences` row matches: `client` = this client; `department` ∈ `Membership.Departments`; `group` ∈ `Membership.Groups`. Application subjects match only `client` rows. A collection decided directly (knowledge routes, download grants) passes in P1 only for an `internal` client or a download grant; other client kinds wait for `CollectionAudiences` (P2) | `audience_mismatch` | 404 |
| 11 | Step 10 needs a department/group row and `Membership.Known = false` | `membership_unavailable` | 503 |
| 12 | Tools (P2): tool policy = tools design §5.1 (server → skill → assistant → client, `ToolPolicy.Visible`/`Authorize`); inside an agent loop a denial is a `denied` tool message + 5002, not an HTTP 403; on `/mcp` the SDK's unknown-tool error (tools design §5.2) | `tool_not_allowed` | 403 / tool message |
| ✓ | Allowed: `EffectiveLevel` = resource level; `MaxResidency` per §4.7 | – | – |

**Assistant versions do not change access** (decided, simplification pass §15). Audience, level (`Classification`),
history mode, retention, owner team and key scopes belong to the **assistant**, not to a version, and are changed only
by their own admin actions (admin design §4.5). A version holds what the assistant answers with (instructions, model,
collections, skills), and publish or restore refuses one that the assistant's level does not cover (admin design §4.6
V3–V6, V8). So steps 1–11 give the same answer before and after a publish or restore, except step 5 on the first
publish; step 12 can change only through the published version's skills and tools, which are bounded by the same
level. Explain access (§4.9) always evaluates the published version.

Steps 5–10 answer **404** (`model_not_found` in the OpenAI error shape for chat), so a caller cannot probe which
assistants exist above its ceiling or outside its audience; the security event still records the real reason.
`Filter` (used by `/v1/models`, `list_collections`) applies the same steps silently, without events.

### 4.4 Errors

Chat-role errors use the OpenAI error shape like the gateway's `GatewayErrors` (`{"error":{"message","type",
"code"}}`); `code` = the reason above, or for authentication the reasons in §6.3/§5.3. Knowledge routes (`/v1/retrieve`,
`/v1/collections`, `/v1/documents/*`) answer RFC 9457 problem details like the gateway's Data API (`ApiFaultException`,
reason in the `code` extension; knowledge design §3.6); the in-process path carries status and code in
`RetrievalDeniedException` and the chat role translates. `WWW-Authenticate: Bearer` on
401 (as `GatewayRequestHandler.Stages.cs:48`), with `resource_metadata` on `/mcp` (§8).

### 4.5 Fail-closed rules

1. No snapshot confirmed within `Config:MaxUnconfirmedSeconds` (deployment setting, default 90 s) ⇒ every
   security-relevant decision (authentication with a key, any `Decide`) is 503 `config_unconfirmed` + event 5004.
   Within the window the last confirmed snapshot decides and `/health/ready` stays ready, so one stalled database does
   not drain every instance at once; beyond it `/health/ready` reports `config_unconfirmed`. Notifications still apply
   changes, revocations included, as soon as they arrive.
2. Unknown client (`azp` not in the registry, key without enabled client) ⇒ refused; there is no default client.
3. A resource or client field that fails to parse (unknown classification, unknown kind) ⇒ the row is treated as
   disabled and logged at error level; never as `public` or "no ceiling".
4. Empty allow-lists mean nothing (no audience rows ⇒ nobody; key scopes empty ⇒ the client's audiences only, never
   "everything in the department").
5. Membership unknown ⇒ only decisions that need it fail (step 11).
6. Open WebUI JWT missing or invalid ⇒ 401, never "act as the connection's application".

### 4.6 Caching

- The policy is pure; its inputs are the version-tagged snapshot, so there is nothing to invalidate in it.
- `/v1/models` and `list_collections` results are memoised in process for 30 s keyed by
  `(ConfigVersion, ClientId, KeyId, MembershipHash)`; a new version makes every entry unreachable (data-model §5:
  caches hold the version they were built from).
- Nothing about a decision is cached in Redis.

### 4.7 Residency ceiling per request

`MaxResidency` comes from the **level of the data in the call** (platform plan §6), not from the client, and is
**always explicit**:

| Effective level | `x-ume-max-residency` |
|---|---|
| `public` | `External` (the worker: `Eu`) |
| `internal` | `Eu` |
| `sensitive` | `OnPrem` |

**The gateway sets each service's maximum** (Decisions "Residency per level"; M-17 C9): the highest of its credential
key's `allowed_residencies` in its own `GET /v1/ume/context` (an empty list allows every residency, `External`; a list
with no value Ume.Rag knows gives `OnPrem`), lowered to the service's fixed maximum (the worker's `Eu`). There is no
residency knob in Ume.Rag's configuration. Every call's ceiling is `min(ForLevel(level), maximum)`: chat (and its
condense call), the admin's V8 `/v1/models` check, model pickers and test panel, and the worker's evaluation. Register
M2 (every model call EU-hosted until on-prem hardware exists) is set on the keys in the gateway: the api's and admin's
allow `OnPrem` and `Eu`, so a `public` assistant is checked and answered at `Eu`; once External routes exist the keys
allow `External` too. Before the first context answer the maximum is `OnPrem` (fail closed), and a call made then waits
up to 10 s for one shared context read rather than running at `OnPrem` right after a start. The context is read again
every 5 minutes, so a key narrowed in the gateway applies here within that time (the gateway enforces it at once). A
call whose ceiling is above the service's maximum is refused before sending (admin `gateway_key_missing`, admin design
§4.6 V8).

**Keys.** From the gateway's integration phase 1 (gateway design §5.1, §13.4) each service holds **one credential key**
of the integration *Ume.Rag* (`rag-api`, `rag-admin`, `rag-worker`; secret file `Gateway__Key`), with `AllowedIpRanges`
and `RequireMaxResidency` (the gateway refuses a request without the header). The api's and admin's allow the
residencies their assistants may use, which is their ceiling (above); the worker's `[OnPrem, Eu]`. Only `rag-admin`'s holds scopes: `organisation.read` (the
whole organisation in the context, §2.1) and, from phase 3, `usage.read`.

**Where the keys come from** (M-17 C7, gateway v0.10.2, its ADR 022). In the Portainer stack the gateway's migration
service provisions the integration and the three keys from the manifest in `UME_INTEGRATION_MANIFEST`
(`stack.env.example`: all three limited to `OnPrem`+`Eu`), **create-only**: an existing integration or key, found by
name, is never changed, a difference is only logged. It writes each new key's secret to the stack's own
`integration-secrets` volume (owner 1654, mode 700, files 600; never `ume-certs`, which holds the CA key), and
`rag-bootstrap`, which mounts that volume read-only with no network, copies each to its service's `Gateway__Key`. A set
`UME_RAG_*_GATEWAY_KEY` wins over the file (keys made by hand, or a gateway elsewhere). Rotation is in the gateway's
admin (with a grace period), then deleting the key's file and updating the stack: a missing file is written again from
the key's stored secret (audited as a reveal). Compose and Kubernetes take the keys from files as before
(`--gateway-credential-key-file`, `create-secrets.sh`). The gateway's preset `deploy/integration-presets/ume-rag.json`
is Ume.Rag's to change (thekim1/llmgateway#95: rag-api and rag-admin limited to `OnPrem`+`Eu` too).

**Acting as the object's key.** From phase 2 (the context's `features` contain `act-as-key`), every call made for an
assistant or a knowledge base that has a `GatewayKeyId` also sends `x-ume-on-behalf-of: <that key id>` (cost-attribution
§4.1 says which object a call belongs to); a call without an object, or for an object without a key, sends none and
runs as the credential key. Each service (api, admin, worker) reads the features from its own `GET /v1/ume/context`
with its credential key, at start and every 5 minutes with `If-None-Match` (gateway-changes §5.2), held in memory;
until the first answer it sends no header. A `2xx` or `304` answer must echo the id sent (the gateway writes it in the
GUID's `D` form; compared as a GUID): a missing or different `x-ume-on-behalf-of` aborts the call before a byte is
used (fail closed, event 5011) while `Gateway:RequireDelegationEcho` is on (default `true`; an older gateway instance
during a rolling update ignores the header and would charge the credential key). An error answer goes by its code:
`403 delegation_not_allowed`, `403 key_disabled`, `403 key_revoked` or `key_expired` and `402 budget_exceeded` mean
the object's key cannot be used now (gateway-changes §5.7; chat §3.6, engine §7.3).

**Two guards on every call.** The ceiling header and the `x-ume-residency` check are Ume.Rag's residency rule on every
call, acted or not, and hold whatever a key's admins change. The gateway is the second guard: a request made as an
assigned key needs `x-ume-max-residency` when either key has `RequireMaxResidency`, and gets the assigned key's
residencies **intersected with the credential key's** (decided by user, 2026-11-12, #580; gateway design §5.3,
built in thekim1/llmgateway#84, feature `credential-residencies`). An older gateway instance during a rolling update
does not apply the bound, which is one more reason the ceiling header goes on every call. Each service's credential key is therefore capped at the highest level that service serves: the worker's at
`Eu` (it never sends `External`), rag-api's and rag-admin's at the highest level their assistants may use (`Eu`
while register M2 holds). The
handler below stays required on every gateway client and tested at every call site.

A `DelegatingHandler` in the gateway client (`ResidencyCeilingHandler`) refuses any request without an explicit
ceiling, or with a ceiling above the service's maximum (the worker: `Eu`), sets `x-ume-on-behalf-of` from the call's object key and checks its echo; it is unit-tested
for every call site (chat, condense, follow-ups, query embedding, rerank, judge, evaluation generation, NER/LLM stages).

Effective level for chat = the assistant's level (≥ all its collections, skills and tool servers' result levels,
validated on save); for condense calls inside a conversation the same. For a client that keeps history (Open WebUI)
**every turn, the first included,** runs at **max(assistant level, client ceiling)**, because earlier turns may come
from another assistant, so model and residency do not change between turn 1 and turn 2 (chat design §8.2, platform
plan §6). For P2 agent runs it is fixed at the run's start and never rises: tool servers are bounded by clearance
(assistant level ≤ clearance) and result level (result level ≤ assistant level) (tools design §5.3).

The Chat API sends the header from `AccessDecision.MaxResidency` and, from phase 2, `x-ume-on-behalf-of` = the
assistant's `GatewayKeyId` on every call of the turn (gateway design §5.2; the FakeGateway echoes too). Header values
are the gateway's
`DataResidency` names (`OnPrem`, `Eu`, `External`, case-insensitive). The gateway answer's `x-ume-residency` is
checked, case-insensitively by order `OnPrem < Eu < External`, against the same value (chat-api §4). Retries are
chat-api §8.3's (`GatewayRetryHandler`): a gateway `502`/`504` is sent once more with the same ceiling; a `503` with
`Retry-After` (in-ceiling targets drained or circuit-open) is not retried but passed on, so the caller can wait and
try again; `403 max_residency_not_met` (no target configured within the ceiling) is never retried.

**Model validation on publish**: when an assistant version is published (and when its level or audience is saved), the
admin calls the gateway's `GET /v1/models` with its credential key and the ceiling of the assistant's effective level
(for an audience client that keeps history, max(level, client ceiling)), from phase 2 as the assistant's key
(`x-ume-on-behalf-of`), and refuses the action if a model the version uses is not listed (gateway design §3.4, §5.5;
admin design V8, V39).

### 4.8 Internal callers

- **Admin test panel**: runs in the admin process, builds a `RagPrincipal` for client `admin-console` with the
  admin's `oid` (fallback `sub`), and checks access with the admin's role scope (department of the assistant ∈ admin's departments),
  not with assistant audiences. Answers are not stored as conversations. It runs the real chat path in process through
  the `Ume.Rag.Answering` library on the assistant's **draft** (or a chosen version), so drafts are tried only here and
  in the admin's assistant test runs, under the same principal (admin design §2.1, §4.9, §7.4). P2 agent test calls go to the hub as `admin-console`
  (needs `ToolPolicy = read` on that client, tools design §2.2).
- **`rag-internal`**: the chat role sends `assistantId` and the already-made decision's level; the knowledge role
  recomputes the assistant's collections from its own snapshot (same version or newer) and refuses if the assistant's
  level is now above what was forwarded (the stricter answer wins).

### 4.9 Explain access (admin, new)

`POST /api/access/explain` `{ clientId, subject: { kind, id, departments?, groups?, permissions? }, assistantId,
action? }` → `{ allowed, step, reason, status, effectiveLevel, maxResidency, configVersion }`. `kind` is `key` (`id` =
the key id), `owui-user` (`id` = the JWT `sub`) or `entra-user` (`id` = the `oid`); `permissions` are an `entra-user`'s
token scopes and default to the delegated scopes (`Chat.Use`, `Knowledge.Read`, `Mcp.Use`); `action` defaults to a
chat turn. The admin API builds a `RagPrincipal` from the input (the client kind's normal credential and permissions,
membership as given with `Known = true`) and calls `IAccessPolicy.Decide` on its current snapshot, with the
assistant's published version read from `config."PublishedAssistantVersions"` as the data plane reads it
(data-model §1.2). `step` is the stable id of the first failing §4.3 step (`0`, `1`,
`1a`, … `12`; an id never changes meaning, a new step gets a new id) and `reason` its stable deny code from the same
table, so the admin UI and support notes can key on both. Because the policy is pure there is no gateway or Graph call
and nothing to clean up; no security event is emitted (nothing is accessed). Who: `rag-platform-admin`, and
`rag-department-admin` for their own department's assistants and clients (`Manage`). The UI is the admin design's.

## 5. Open WebUI trust

### 5.1 What Open WebUI sends

With `ENABLE_FORWARD_USER_INFO_HEADERS=true` and `FORWARD_USER_INFO_HEADER_JWT_SECRET` set, every request to a
connection carries `X-OpenWebUI-User-Jwt` (`backend/open_webui/env.py:997-1016`), an HS256 JWT minted per request
(`backend/open_webui/utils/headers.py:41-52`). Line numbers in this section were taken at **v0.12.3**; the pin
is **v0.12.5** (A-4, #457), which keeps the JWT and the settings below as designed (to-verify #46):

```json
{ "sub": "<Open WebUI user id>", "email": "...", "name": "...", "role": "user|admin|pending",
  "iss": "open-webui", "iat": 1761730000, "exp": 1761730300 }
```

There is **no `aud`**, and if minting fails Open WebUI silently falls back to the unsigned `X-OpenWebUI-User-*`
headers (`headers.py:70-86`). The chat id travels unsigned in `X-OpenWebUI-Chat-Id` (`headers.py:196-198`). The
connection key goes in `Authorization: Bearer` (`headers.py:210-228`) unless `auth_type` says otherwise.

Open WebUI has **one connection** (ours, chat design §4.2–§4.3); its title, tag and follow-up generation is switched
off (`../archive/passes/simplification-pass.md` §1), so there is no gateway connection and the JWT goes only to `rag-api`. The
connection also sends the custom headers `X-Rag-Owui-Message-Id` and `X-Rag-Owui-User-Message-Id` (templated by Open
WebUI, `headers.py:138-155`). They are **unsigned**: they only map messages and feedback **inside the caller's own
conversation** (keyed by the JWT `sub`), never grant access.

### 5.2 Validation (`OpenWebUiUserJwt`, new)

1. The request authenticated with an `ume-rag-sk-` key whose client has `Kind = open-webui` (§6). The JWT is never a
   credential on its own: replaying a captured JWT without the connection key gets nothing.
2. Header present and ≤ 4 KB; exactly one.
3. Header `alg` must be `HS256` (pinned `ValidAlgorithms = ["HS256"]`; `none`, `RS*`, `ES*` and a `kid` are refused).
   Key: `OpenWebUi:JwtSecret` (secret file `OpenWebUi__JwtSecret`, ≥ 32 bytes, generated by `init-deployment.sh`,
   repo-and-deployment §7.1). The JWT is trusted only for **the deployment's Open WebUI client**: the client of kind
   `open-webui` marked `IsManagedByDeployment` (Decisions "Deploy configuration"; M-17 C4). The deployment generates
   its `ume-rag-sk-` key (`rag-bootstrap` in the Portainer stack, `init-deployment.sh` with Compose) and gives it to
   Open WebUI and to `rag-migrate`, which creates or updates that client and keeps exactly that key active on it
   (`OpenWebUi:ApiKey`, a secret file of rag-migrate). No such client = not configured: rag-api starts, and no Open
   WebUI user JWT is trusted. An `open-webui` client made by hand in the admin is never trusted with user JWTs. A
   second instance later gets its own managed client and `JwtSecret`: the decided fallback for a second department is
   one Open WebUI instance per department, each its own client with its own ceiling and audience, until W3 and E1 are
   solved (weakness review §5, plan §5).
4. `iss = "open-webui"`; `exp` and `iat` required; clock skew 30 s; `exp − iat ≤ OpenWebUi:MaxLifetimeSeconds`
   (600; Open WebUI's default is 300, `env.py:1012-1016`); `iat` not more than 30 s in the future.
5. `sub` non-empty, ≤ 100 characters, printable; `role` ∈ {`user`, `admin`} (`pending` refused).
6. Plain `X-OpenWebUI-User-*` headers are **never** read. If they are present without a JWT (the fallback above) the
   request is refused with `user_jwt_missing`, so a misconfigured secret is noticed at once.

Failures: 401, reasons `user_jwt_missing`, `user_jwt_invalid` (signature, alg, issuer), `user_jwt_expired`,
`user_jwt_role` — all counted in the `rag.auth.failed` buckets (§10) with the key id.

### 5.3 Network and secret hygiene

- The connection key has `AllowedIpRanges` = the Open WebUI host (gateway pattern, `address_not_allowed`,
  `GatewayRequestHandler.Stages.cs:63-67`); the firewall admits only that host to `rag-api` for this path.
- Exception: the reverse proxy exposes `GET /v1/documents/*` and `GET /v1/documents/*/content` to the **staff network**,
  so users' browsers can follow citation download links (signed grants, knowledge design §4.6); every other `rag-api`
  path stays restricted to the Open WebUI host.
- `FORWARD_USER_INFO_HEADER_JWT_SECRET` is used for nothing else. Open WebUI sends the same JWT to every connection
  (forwarding is global); it has **one connection only** (ours; task generation is off, chat design §4.2), so the JWT
  reaches only `rag-api`. Without
  `aud` a receiver could replay it within its lifetime, but it is useless without our connection key (§5.2 step 1);
  any connection added later must be a municipal service, never a third-party endpoint.
- The feedback import (chat design §14.2) calls Open WebUI's admin API with a service account's API key
  (`OpenWebUi__AdminApiKey`); Open WebUI restricts every API key to `/api/v1/evaluations/feedbacks/list`, and
  `ENABLE_ADMIN_CHAT_ACCESS=false` keeps its admins out of users' chats (chat design §4.3). The service account is a
  local Open WebUI account, the store's first user and only admin
  (`WEBUI_ADMIN_EMAIL`/`WEBUI_ADMIN_PASSWORD`), not a Keycloak account; its key reaches the feedback list, the per-id
  feedback routes and feedback creation (prefix match).
- Open WebUI settings required for user trust: `ENABLE_LOGIN_FORM=false` and `ENABLE_SIGNUP=false` (only OIDC
  login; `backend/open_webui/config.py:1692-1694`), `ENABLE_PASSWORD_AUTH=false` (the form setting only hides the
  form and does not block API password sign-in; always off, since the deployment's setup job mints the service
  account's key without it, chat design §14.2),
  `DEFAULT_USER_ROLE=user` (with the default `pending` and no person as admin, no OIDC account could chat), `ENABLE_OAUTH_SIGNUP=true` (`:2563`),
  `OAUTH_MERGE_ACCOUNTS_BY_EMAIL=false` (`:2570`),
  `OPENID_REDIRECT_URI=https://<owui host>/oauth/oidc/login/callback` (`:2629`; the `oidc` provider registers only
  with `OAUTH_CLIENT_ID`, `OPENID_PROVIDER_URL` and a client secret or PKCE method, `:2792`). The callback route
  is `/oauth/{provider}/login/callback`; the older `/oauth/{provider}/callback` is still served as a "legacy endpoint"
  (`backend/open_webui/main.py:2932-2933` at v0.12.3). `deploy/compose.openwebui.yaml` (`OPENID_REDIRECT_URI`), the dev
  realm (`open-webui-dev`), the Entra Bicep and the P1 realm additions (`open-webui`, §11.2) all use
  `/oauth/oidc/login/callback`; they moved together at the go-live pin bump to v0.12.5 (A-4, #457), before the legacy
  route is removed (Decisions table "P0 integration, stream A"; to-verify #46).
- **Provider per phase.** P1: the shared Keycloak, client `open-webui` in realm `ume` (§11.2), with
  `OPENID_PROVIDER_URL=https://<keycloak host>/realms/ume/.well-known/openid-configuration`
  (`UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL`, `init-deployment.sh --openwebui-oidc-provider-url`), `OAUTH_CLIENT_ID=open-webui`
  and that client's secret in `OAUTH_CLIENT_SECRET`; `deploy/compose.openwebui.yaml` takes the provider URL and the
  provider name (`UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME`, default "Umeå kommun") from `deploy/.env`. P2:
  the Entra registration `ume-rag-open-webui` (§12.1), where *assignment required* on its enterprise app decides who
  can sign in (plan §5).
- **Who may sign in to Open WebUI in P1** is not settled. Keycloak has no per-client assignment: every enabled
  account of realm `ume` (the gateway's admins too) can complete Open WebUI's sign-in and gets an Open WebUI account
  (`ENABLE_OAUTH_SIGNUP=true`) with the role `user` (`DEFAULT_USER_ROLE=user`). Candidates, not decided: Open WebUI's own role gate (`ENABLE_OAUTH_ROLE_MANAGEMENT`,
  `OAUTH_ROLES_CLAIM`, `OAUTH_ALLOWED_ROLES`; present in the local checkout at v0.12.5, `config.py:2683`, `:2698`,
  `:2702`, to verify at the pin) with a pilot-user role in the token, or a Keycloak browser-flow condition on the
  `open-webui` client (§16).

### 5.4 User mapping

- `SubjectKind = OwuiUser`, `SubjectId = sub`. Email and name from the JWT live only in the request's principal
  (display); they are never stored or logged, and never the basis of a grant.
- Without the JWT: `GET /v1/models` answers the **client-level** list (assistants with a `client` audience row for
  this client, level ≤ ceiling), because Open WebUI also lists models outside a user request (`routers/openai.py:94-105`
  sends user headers only when it has a user). Chat completions and feedback need the JWT (401 otherwise).
- `X-OpenWebUI-Auth-Type: api_key` (a user calling Open WebUI's API with a personal key, `headers.py:65-67`) is
  accepted; the subject is still the user.
- Department and group audiences for Open WebUI users wait for E1 (a real provider token through Open WebUI, §9.4);
  until then Open WebUI users match `client` audience rows only. That holds in P1 on Keycloak too: Open WebUI's sign-in
  token is not forwarded to us, and the JWT carries no departments.

## 6. Key authentication

### 6.1 Format and storage (adapt `VirtualKeyHasher`)

| | Gateway | Ume.Rag |
|---|---|---|
| Secret key | `ume-sk-` + 43 base62 (`VirtualKeyHasher.cs:9-17`) | `ume-rag-sk-` + 43 base62 |
| Public (widget) key | – | `ume-rag-pk-` + 43 base62 |
| Display prefix | 11 chars (`:19`) | 15 chars (`ume-rag-sk-AbC1`) |
| Hash | hex HMAC-SHA256(pepper, key) (`:46-51`), pepper ≥ 32 bytes (`:23-31`, `Security.cs:13-18`) | same, **own** pepper `Security:KeyPepper` (never the gateway's) |
| Shape check | `LooksLikeKey` (`:65-69`) | `LooksLikeKey` accepts either prefix; the stored `Keys.Kind` must match the prefix (else `invalid_key`) |
| Reveal | `EncryptedSecret` via Data Protection, gateway-admin only, audited (`KeyEndpoints.cs:116-125`) | same, `rag-platform-admin` only; `Copy`/`Reveal` audit actions |
| Rotation | `KeyRotation.Rotate`: new key copies policies, old key revoked now or `GraceUntil = now + 24 h` (`KeyRotation.cs:7`, `:54-101`) | copy (`Scopes`, origins, IP ranges, rate limits copied) |
| Status | `GetStatus`: revoked → disabled → expired → grace/revoked (`Entities.cs:118-141`); usable = active or in grace (`:143`) | copy, plus "client disabled" → `owner_inactive` (like inactive team/department, `GatewayRequestHandler.Stages.cs:58-59`) |

Distinct prefixes mean neither system accepts the other's keys (they also never share a pepper), and a gateway key
sent to us is reported as `invalid_key` without logging anything derived from it.

### 6.2 Lookup and cache (adapt `KeyAuthenticator`)

Copied behaviour (`Gateway/Pipeline/KeyAuthenticator.cs`):

- Extract from `Authorization: Bearer`, `x-api-key`, `api-key` (`:54-73`).
- Positive cache (100 000 entries) with stale-while-refresh after `KeyCacheSeconds` (30), single-flight loads per
  hash (`:79-143`); negative cache of unknown hashes for the same TTL in a separate, smaller cache (10 000) so made-up
  keys cannot evict real ones (`:13-20`, `:99-102`, `:164-177`); a load that started before an invalidation is
  neither cached nor shared (`:107-125`, `:157-193`).
- `FindCached` for throttled addresses (`:76-77`).

Adapted: invalidation comes from the **config version** instead of the gateway's Redis invalidation bus (`:45-51`):
the config monitor calls `InvalidateAll()` (`:195-200`) whenever an outbox row with `EntityType` `Keys` or `Clients`
arrives (data-model §5; Redis has no channels in our design, data-model §11). A missed `NOTIFY` is covered by the 2 s
poll, so a revoked key stops working within seconds while the database is reachable; if it is not, the last confirmed
snapshot serves for at most `Config:MaxUnconfirmedSeconds` (§4.5), then keyed requests get 503. Creating a key bumps the version and clears the negative cache, as in the
gateway (`docs/security-events.md:35-37`).

Settings mirror `Gateway:*` (`Gateway/Pipeline/GatewayOptions.cs:12-22`, `docs/security-events.md:39-45`):

| Setting | Default |
|---|---|
| `Api:KeyCacheSeconds` | 30 (0–300) |
| `Api:AuthFailuresPerMinute` | 60 (0 = off) |
| `Api:Security:SourceAddress` | `Truncated` |
| `Api:Security:AuthFailureFlushSeconds` | 10 |
| `Api:Security:MaxAuthFailureBuckets` | 1 000 |

`LastUsedAt` goes to `config."KeyLastUse"` at most every 5 minutes per key (as `DataApiKeyStore.TouchAsync`,
`DataApi/DataApiKeyAuthentication.cs:63-86`), so key use never bumps the config version (data-model §2.1).

### 6.3 Request path (adapt `GatewayRequestHandler.AuthenticateAsync`)

Same order as `GatewayRequestHandler.Stages.cs:21-77`:

1. Address over `AuthFailuresPerMinute` ⇒ only cached keys, else 429 `rate_limited` with `Retry-After`
   (`AuthFailureThrottle`, copied as is: one-minute windows, IPv6 per /64, ≤ 100 000 tracked addresses,
   `AuthFailureThrottle.cs:7-106`; keyed by the full IPv4 address or the IPv6 /64, independent of the truncated
   address the events record). Every key or client change empties the key cache (a cached key may just have been
   revoked), so for the rest of the minute a throttled address gets 429 for a valid key too.
2. Unknown or missing key ⇒ throttle count + failure bucket (`missing_key` / `invalid_key`), 401.
3. Status: `key_revoked`, `key_disabled`, `key_expired`, `owner_inactive` (team inactive in the mirror, or client
   disabled) ⇒ failure bucket with key id, 401/403.
4. `address_not_allowed` after the status checks, so a revoked key is reported as revoked wherever it is used
   (`:63-67`). The address is the client's: rag-api's `ClientAddressMiddleware` (copied from the gateway) takes it from
   `X-Forwarded-For` only when the connection comes from one of `Settings.TrustedProxies` (read from the access
   snapshot), else the connection address. Until the snapshot's first load, seconds after start, the list is empty
   and the proxy's address is used.
5. **New for `pk` keys**: `origin_not_allowed` (§6.5).
6. Config confirmed (§4.5), then the client layer (Open WebUI JWT, §5) and the policy.

Failures are aggregated exactly as the gateway's `AuthFailureRecorder` (bucket = reason, endpoint, key id, address;
flushed every 10 s, capped buckets with one overflow row, `AuthFailureRecorder.cs:23-48`,
`docs/data-access.md:85-105`) into a new `audit."AuthFailures"` table (§14) and one `rag.auth.failed` event per bucket.
Our bucket adds `Credential` and `ClientId`. The presented value is never stored or logged (`docs/data-access.md:103-105`).
There is no `audit."AuthFailures"` table yet: the knowledge role aggregates in memory and emits 5001 once a minute per bucket with the count since the last emit, and a flush every
10 s reports failures held back after the last emit once their minute has passed (idle buckets are dropped). The
table comes with a schema PR.

### 6.4 Admin side

Keys page copies the gateway's endpoints (`KeyEndpoints.cs:79-133`): create (secret shown once), update, rotate
(immediate or 24 h grace), reveal/copy (platform admin, audited), revoke. Who may manage: `rag-platform-admin`
everywhere, `rag-department-admin` for keys of teams in their departments (`AdminContext.KeyTeams` pattern,
`AdminContext.cs:102-113`). Every change is `rag.admin.change`. Adapted from the gateway: a key belongs to a client,
with an optional team for attribution and management; only a platform admin creates a key without a team (platform
integrations). P1 creates `sk` keys only. Keys carry no `version` (rotate and revoke check the key's own state), and
errors carry admin codes (`key_not_rotatable`, `key_revoked`, `key_secret_unavailable`, `key_scope_invalid`, and
`client_managed_by_deployment` for a key of the deployment's own client, §5.2, which only the deployment creates,
rotates or revokes).

### 6.5 Widget `pk` keys (P2)

**Later (P2):** a `pk` key is public (it is in the page source), so it identifies the widget, not a secret holder:

- `Origin` must be present and match exactly (scheme, host, port; no wildcards) one entry of
  `Keys.AllowedOrigins ∩ Assistants.AllowedOrigins` of the requested assistant; else 403 `origin_not_allowed`. CORS
  response headers are sent only for that origin; `OPTIONS` preflight answered without authentication but only for
  allowed origins.
- Only `ListModels` (its scoped assistants), `Chat` and `Feedback`; only `public` assistants (§4.3 steps 4, 8).
- Anonymous session: on the first chat response the API issues `x-rag-session` (32 random bytes, base64url); the
  widget keeps it in `sessionStorage` and sends it back. `SubjectId = anon:` + hex HMAC-SHA256 of it with a key derived
  from the pepper (`HKDF(pepper, "widget-session")`), so the stored id cannot be turned back into the token.
- Rate limits per key and per client address through `IRateLimitStore`: in process on one host; with
  `ConnectionStrings:redis` (several hosts) `ume-rag:rl:req:{keyId}:{minute}` and `ume-rag:rl:ip:{ipHash}:{minute}`
  (data-model §11).
  `Origin` can be forged by non-browser clients, so the per-address limit and the `public`-only rule are the real
  protection.

## 7. OAuth tokens: machine clients and delegated users

### 7.1 Configuration (adapt `DataApiAuthentication`)

| Setting | Env | Default | Gateway counterpart |
|---|---|---|---|
| `RagApi:Authority` | `UME_RAG_API_AUTHORITY` | `UME_RAG_OIDC_AUTHORITY` | `DataApi:Authority` (`DataApiOptions.cs:15`, `compose.prod.yaml:181`) |
| `RagApi:Audience` | `UME_RAG_API_AUDIENCE` | `ume-rag-api` (Keycloak, dev and P1: the `ume-rag-api` audience mapper, §11; P2 Entra v2: the `rag-api` application id) | `DataApi:Audience` = `ume-data-api` (`DataApiOptions.cs:31-32`) |
| `RagApi:PermissionClaims` | – | `scope`, `scp`, `roles` | `DataApiOptions.cs:38` |
| `RagApi:TenantId` | `UME_RAG_API_TENANT_ID` | `UME_RAG_OIDC_TENANT_ID` | new; unset in P1 (Keycloak has no `tid`, §1.7) |
| `RagApi:RefuseGuests` | – | `true` | new |
| `RagApi:AllowKeys` | – | `true` | `DataApi:AllowKeys` (`:18`) |

Authentication setup is the Data API's (`DataApiAuthentication.cs:21-64`): a policy scheme forwards to the key
handler when the request presents something shaped like our key (`PresentsKey`, `DataApiKeyAuthentication.cs:124-130`,
adapted to both prefixes and to `api-key`), otherwise to JWT bearer with issuer, audience and lifetime validation,
`MapInboundClaims = false` (`DataApiAuthentication.cs:33-47`). Tokens are accepted only if `RagApi:Authority` is set;
without it only keys work, as in the Data API (`:26-31`). A key's permissions and a token's never mix (`:66-68`).

Added checks after signature/issuer/audience/lifetime (JWKS cached by the handler's configuration manager):

1. `tid = RagApi:TenantId` (when set) ⇒ else `wrong_tenant`.
2. Guests refused by the §1.7 rule (`RagApi:RefuseGuests`) ⇒ `guest_refused` (delegated tokens only).
3. Client = `ClientId(user)` (`client_id` ?? `azp` ?? `appid`, `DataApiAuthentication.cs:80-85`, without the `sub`
   fallback) looked up in `Clients.OAuthClientId`; unknown ⇒ `client_unknown`.
4. Token type: **app-only** when `idtyp = app` (Entra optional claim, requested on `rag-api`) or, on Keycloak, when the
   token has `client_id` and no `sid` (client-credentials tokens have no session; confirmed on Keycloak 26.6.4 in P0,
   to-verify #83, provided `service_account` is in the client's default scopes). Otherwise delegated.

**P1 (shared Keycloak).** Not built yet (there is no `RagApi` code). `RagApi:Authority` defaults to the admin's authority,
so in P1 it is realm `ume` of the shared Keycloak, and `RagApi:Audience` stays `ume-rag-api`: RAG's realm additions
define a client `ume-rag-api` (§11.2) as the audience, so a token for the gateway's Data API (`aud` `ume-data-api`)
never passes our audience check and ours never passes the gateway's. P1 has no token caller (every token row in §3 is
P2 or later; integrations use keys), so a token that does validate still ends at step 3 with `client_unknown` until a
platform admin registers its `azp` in `config."Clients"`. Which token-caller parts go into the P1 additions is open
(§16).

### 7.2 Permissions

| Value | Type | Claim | Grants | Subject |
|---|---|---|---|---|
| `Chat.Use` | delegated scope | `scp` | `/v1/models`, `/v1/chat/completions`, feedback, as the user | `EntraUser` |
| `Knowledge.Read` | delegated scope | `scp` | `/v1/retrieve`, `/v1/collections`, documents, as the user | `EntraUser` |
| `Mcp.Use` | delegated scope | `scp` | `/mcp` (§8) | `EntraUser` |
| `Chat.Invoke.All` | app role (members: Applications only) | `roles` | chat as an application, client audiences only | `Application` |
| `Knowledge.Read.All` | app role (Applications only) | `roles` | search as an application, client audiences only | `Application` |

Delegated permissions are read only from `scp`/`scope` and app permissions only from `roles`, so a user who happens to
have an app role assignment cannot act as an application, and an app token cannot carry a user scope. App roles are
restricted to `allowedMemberTypes: ["Application"]` in Entra. Keycloak has no such restriction: there they are realm
roles given only to service accounts (dev: `service-account-ume-rag-api-dev`), and a user given one by mistake still
cannot act as an application (§13.4); with *full scope allowed* off, a client's tokens carry them only if the client's
role scope mappings include them. Distinct names avoid any clash between an app role and a
scope value on one registration. This is stricter than the Data API's "any configured claim" rule
(`DataApiAuthentication.cs:71-77`), because the Data API has no user tokens.

## 8. MCP protected-resource metadata (P2)

**Later (P2):** written for Entra, the P2 provider ("Identity provider per phase" at the top); nothing here is used in P1.

- Endpoints: `GET /.well-known/oauth-protected-resource/mcp` (RFC 9728 §3.1 path form for resource
  `https://<api host>/mcp`) and one per skill endpoint (`/.well-known/oauth-protected-resource/mcp/skills/<slug>`),
  anonymous, cacheable 1 h:

  ```json
  { "resource": "https://<api host>/mcp",
    "authorization_servers": ["https://login.microsoftonline.com/<tid>/v2.0"],
    "scopes_supported": ["api://<rag-api app id>/Mcp.Use"],
    "bearer_methods_supported": ["header"],
    "resource_name": "Umeå kommun – kunskap" }
  ```

- 401 on `/mcp` without a valid token: `WWW-Authenticate: Bearer resource_metadata="https://<api host>/.well-known/
  oauth-protected-resource/mcp", scope="api://<rag-api app id>/Mcp.Use"`; 403 `error="insufficient_scope"` when
  `Mcp.Use` is missing.
- Token validation as §7 (aud = `rag-api` application id, `tid`, guests refused, `scp` contains `Mcp.Use`). Entra v2
  puts the application id in `aud`, not the resource URL, so audience binding is to `rag-api` as a whole.
- **Pre-registered clients only**: Entra has no dynamic client registration. Each MCP client application (by its
  Entra application id) must be in `config."Clients"` with `Kind = mcp-client`, ceiling `public` by default, raised to
  `internal` only by a platform admin (never `sensitive`, `CK_Clients_Mcp`); unknown `azp` ⇒ 403 `client_unknown`.
  Consent for `Mcp.Use` is granted by an admin per client (or `preAuthorizedApplications` on `rag-api`), not by users.
- Per-skill endpoints narrow further: only that skill's tools and collections, same principal and policy; their audience
  is `config."SkillAudiences"` (tools design §7.4).
- Both kinds of metadata document and the challenge are served by the MCP SDK's `McpAuthenticationHandler` at its
  default `ResourceMetadataUri` (same paths as above); our JWT bearer configuration (§7) authenticates (tools design
  §7.3; composition *to verify*).
- **M1 unknowns** (postponed-decisions M1, decide at P2 start): whether MCP clients accept Entra's OIDC discovery
  document in place of RFC 8414 metadata; whether Entra v2 rejects the RFC 8707 `resource` parameter that MCP clients
  send (AADSTS error) or ignores it; Client ID Metadata Documents support in Entra and clients; which first-party client
  ids (VS Code, Copilot Studio, Claude) can be pre-authorised. Each is tested with the real clients before P2 ships.

## 9. On-behalf-of (P3, design only)

**Later (P3):** Entra only (the exchange is Entra's OBO flow); it assumes the P2 move to Entra.

### 9.1 Hand-off

1. A caller with a delegated `rag-api` token (own client, Teams, MCP; Open WebUI only after E1) reaches the Chat API;
   the token was validated (§7).
2. For a tool call whose tool server has `AuthMode = obo`, the Chat API sends the raw access token to the tool hub in
   the internal header `x-rag-user-assertion` on the internal, mTLS-only hop that already carries the service key.
   It is held only in request memory: never stored, never in a log scope, redacted by the HTTP logging filter, never
   in a trace attribute.
3. The tool hub checks that the assertion's `oid` equals the principal's `SubjectId` forwarded with the call, and that
   it still has ≥ 2 minutes to live; else `claims_challenge` (§9.3) asking the client to re-authenticate.

### 9.2 Exchange and cache

- The exchange uses the **`rag-api` registration's credential** (decided, plan §7: Entra requires the assertion's
  audience to be the exchanging app), a certificate on-prem, via MSAL `AcquireTokenOnBehalfOf(scopes, new
  UserAssertion(token))` with the downstream scopes registered on the tool server row.
- Cache: MSAL's in-memory token cache in the tool hub, partitioned by assertion hash, entries ≤ the downstream token's
  lifetime, process-local only (no Redis, so nothing to encrypt at rest); a restart costs one exchange per user.
  Should several tool-hub instances ever need a shared cache, it is Redis with Data Protection encryption and a key
  per user (plan §7).
- P2 service-identity calls (client credentials) run in the in-process hub of `rag-api` and `rag-admin` (tools design
  §4.6); OBO arrives with the separate `rag-tools` service (P3, tools design §2).

### 9.3 Claims challenges and consent

- `invalid_grant`/`interaction_required` with a `claims` value (Conditional Access, MFA step-up) ⇒ the tool hub returns
  a typed error; the Chat API ends the turn with an error event and, for HTTP clients, `WWW-Authenticate: Bearer
  error="insufficient_claims", claims="<base64>"` so MSAL-based clients (own client, Teams) can re-acquire. MCP: the
  same header on the MCP HTTP response.
- `consent_required` ⇒ shown as "Verktyget kräver administratörsgodkännande"; consents are granted by admins
  (approval package "later", plan §2), never by users.
- Event `rag.obo.refused` (5010) with the reason; never the token.

### 9.4 Open WebUI (E1)

With `auth_type: system_oauth` Open WebUI forwards the user's own OAuth access token in `Authorization`
(`headers.py:211-214`), so the connection key moves to `x-api-key` (custom connection header, accepted by the key
extraction in §6.2). For this to be a `rag-api` token, Open WebUI's login must request `api://<rag-api app id>/Chat.Use`
and refresh it. Then: subject = `EntraUser` (`oid`), the JWT is still validated and its email must match the token's
`preferred_username`/`email` (else `user_jwt_mismatch`), the client stays the Open WebUI client (ceiling `internal`).
The E1 spike is P2, with Entra (Decisions table "P1b briefs", O3; §16 question 9). Until it passes, OBO tools are offered only to clients that send a real Entra
token.

## 10. Security events

Copied pattern: one static partial `SecurityEvents` class in `Ume.Rag.Infrastructure`, category `Ume.Rag.Security`,
`[LoggerMessage(id, level, …, EventName = …)]` with level as constructor argument for CA1873
(`SecurityEvents.cs:63-114`), `Printable()` on every client-supplied value (`:30-49`), names and attributes only ever
added (`:7-11`, `docs/data-access.md:58-62`). Never logged: key material, tokens, JWTs, passwords, prompts, emails,
display names, group names. Allowed identifiers: key id + display prefix, client id, `SubjectId` (oid / Open WebUI id /
key id / anonymous HMAC), admin `oid` (fallback `sub`, which is what P1 on Keycloak records; the gateway's `Actor` is
`sub`), truncated source address
(`Api:Security:SourceAddress`).

Ranges: 5001–5099 data-plane identity and access; 5101–5199 content and ingestion; 6001–6099 admin. Repo-and-deployment
§9 ids are kept unchanged; new ids are additions.

| Id | Name | Emitted by | When | Attributes |
|---|---|---|---|---|
| 5001 | `rag.auth.failed` | api, admin (sign-in refusals) | Once per bucket per flush (§6.3); admin: per refused sign-in | `Reason`, `Credential` (`sk`, `pk`, `token`, `owui-jwt`, `oidc-signin`, `grant`), `Endpoint`, `KeyId`, `KeyPrefix`, `ClientId`, `TeamId`, `DepartmentId`, `SourceAddress`, `Count`, `FirstSeen`, `LastSeen` |
| 5002 | `rag.access.denied` | api | `IAccessPolicy` denial on an explicit request (not `Filter`), at most once a minute per (subject, reason, resource) with `Count` (like `AccessDeniedThrottle`, `AdminActivity.cs:172`) | `Reason`, `Action`, `ResourceType`, `ResourceId`, `SubjectKind`, `SubjectId`, `ClientId`, `KeyId`, `ConfigVersion`, `SourceAddress`, `Count` |
| 5003 | `rag.residency.refused` | api, worker | gateway answered with a residency above the ceiling | (as step 2) |
| 5004 | `rag.config.stale_refused` | api, worker | `config_unconfirmed` refusal, once a minute per instance with `Count` | `Service`, `CachedVersion`, `LastConfirmedAt`, `Count` |
| 5005 | `rag.ceiling.refused` | api | step 7 denial (instead of 5002) | 5002's attributes + `ResourceLevel`, `ClientCeiling` |
| 5006 | `rag.auth.throttled` | api | an address crossed `AuthFailuresPerMinute` (once per address per window) | `SourceAddress`, `Endpoint`, `Count` |
| 5007 | – | – | reserved for the P2 Graph member-group lookup (§17) | – |
| 5008 | `rag.key.unexpected_use` | api | a key in grace period used after its replacement was used (leak signal), once per key | `KeyId`, `KeyPrefix`, `RotatedToKeyId`, `SourceAddress` |
| 5009 | `rag.mcp.client_refused` | api | delegated token from an `azp` not registered as an enabled `mcp-client` | `ClientId` (azp), `SubjectId`, `SourceAddress` |
| 5010 | `rag.obo.refused` | tools (P3) | OBO exchange failed (`claims_required`, `consent_required`, `assertion_expired`, `error`) | `Reason`, `ToolServerId`, `SubjectId`, `ClientId` |
| 5011 | `rag.gateway.delegation_missing` | api, admin, worker (Warning) | a `2xx` or `304` response lacks the `x-ume-on-behalf-of` echo of the key id sent, or echoes another GUID, and `Gateway:RequireDelegationEcho` is on; call aborted before the first byte (chat design §8.2; error answers go by their code, gateway-changes §5.7) | `Step`, `AssistantId` or `CollectionId`, `GatewayKeyId`, `GatewayRequestId` |
| 5012 | `rag.tool.definition_changed` | api, admin (Warning, P2) | tool definition drift observed, once per (tool, observed hash) (tools design §4.4) | `ToolServerId`, `ToolId`, `ToolName`, `Kind` (`changed`/`removed`/`added`/`instructions-changed`), `PinnedHash`, `ObservedHash` |
| 5013 | `rag.tool.upstream_refused` | api, admin (Warning, P2) | a tool server refused our credential or it could not be used, once a minute per (server, reason) with `Count` | `ToolServerId`, `Reason` (`unauthorized`, `forbidden`, `token_failed`, `credential_unavailable`, `tls`), `Status`, `Count` |
| 5014 | `rag.content.tag_neutralised` | api, admin (Information, P2) | reserved tags (tools design §3.8) neutralised in untrusted text, once a minute per (kind, resource) with `Count` | `SourceKind` (`source`, `tool_result`, `tool_description`, `skill_description`), `ResourceId`, `Count` |
| 5015 | `rag.tool.result_refused` | api, admin (Warning, P2) | a tool result refused: `too_large`, `level_refused`, `protocol_error` | `ToolServerId`, `ToolId`, `Reason`, `ResultBytes`, `ResultLevel`, `EffectiveLevel` |
| 5101–5103 | `rag.egress.blocked`, `rag.malware.detected`, `rag.pii.flagged` | worker | 5101/5102: connectors design §7, §11; 5103 on every transition to flagged (processing design §9.5) | 5103: `DocumentId`, `ChunkSetId`, `Classification`, `Types`, `Counts` (numbers only), `SourceId`, `RunId`, `Reflag`; SIEM rule for public references with a personnummer. Admin audit action `pii.clear` (a decision on the document) |
| 5104 | `rag.parser.limit_exceeded` | worker | parser refused a file for a bomb or size limit (parser design §7.5) | `DocumentId`, `Limit` |
| 5105 | `rag.pipeline.refused` | worker | refused to run a published pipeline version that fails validation (engine design §17) | `PipelineId`, `PipelineVersionId`, `Reason` |
| 5106 | `rag.malware.scan_off` | worker (Warning) | at every start with `ClamAv:Mode = off`: files are stored without a malware scan (connectors design §11) | – |
| 6001 | `rag.admin.change` | admin | every audit entry (gateway 2001) | `Actor`, `Action`, `EntityType`, `EntityId` |
| 6002 | `rag.admin.signin` | admin | sign-in, not extension (gateway 2002, `AdminAuthentication.cs:100-107`) | `Actor`, `SourceAddress`, `Roles`, `Departments`, `SessionId` |
| 6003 | `rag.admin.signout` | admin | (gateway 2003) | `Actor`, `SourceAddress`, `SessionId` |
| 6004 | `rag.conversation.access` | admin | conversation review events: comment reveal and conversation access, before decrypting; per fetch within an access window (admin design §8.2–§8.3) | `Actor`, `RequestId`, `Event`, `Scope` (`comment`/`conversation`), `ConversationId`, `Classification`, `AssistantId` |
| 6005 | `rag.admin.session.extended` | admin | *Stay signed in* (gateway 2004) | `Actor`, `SessionId`, `ExpiresAt`, `SourceAddress` |
| 6006 | `rag.admin.access.denied` | admin | 403 for a signed-in user, once a minute per user (gateway 2005) | `Actor`, `Method`, `Path`, `Count`, `SourceAddress`, `Roles` |
| 6007 | `rag.admin.data.read` | admin | reads of the audit log, keys, conversation lists, feedback lists, the access log and conversation views (gateway 2006) | `Actor`, `Path`, `Query`, `SourceAddress` |

`Reason` values of 5001: the gateway's set (`missing_key`, `invalid_key`, `key_revoked`, `key_expired`,
`key_disabled`, `owner_inactive`, `address_not_allowed`; `SecurityEvents.cs:51-61`) plus `origin_not_allowed`,
`user_jwt_missing`, `user_jwt_invalid`, `user_jwt_expired`, `user_jwt_role`, `user_jwt_mismatch`, `token_invalid`,
`token_expired`, `wrong_tenant`, `guest_refused`, `client_unknown`, `permission_missing`, `grant_invalid`,
`grant_expired` (download grants, knowledge design §4.6). 5002 `Reason` = §4.3 reasons plus `conversation_not_owned`
(an issued conversation id whose MAC does not match the caller's client and subject, chat design §11.1) and, for tool
policy denials (P2, `ResourceType` `tool`/`skill`, attributes + `ToolId`, `SkillId`), `tool_not_allowed`,
`tool_grant_missing`, `tool_clearance`, `tool_unapproved`, `tool_drifted`, `tool_write_disabled`, `client_tool_policy`,
`skill_audience` (tools design §9.3). The MAC key
is `Security:ConversationIdKeys` (secret files `Security__ConversationIdKeys__<kid>`, ≥ 32 bytes, made by
`init-deployment.sh`; newest signs, all verify, as the download-grant keys).

Assistant versions and owner-team scoping (simplification pass §15) add **no event**: publish, restore, copy and
archive are admin changes (6001 with `assistant.publish|restore|copy|archive`, admin design §9.2), a refused
owner-team write is a 403 (6006), and a draft never reaches the data plane, so it cannot cause a 5xxx event.

Gateway counterparts (gateway design §10): our 5001 with `wrong_tenant`/`guest_refused` = gateway 2008 (admin) or
3003 (Data API).

SIEM alerts to add to the gateway's list (`docs/security-events.md:29-34`): any 5008; 5005 from one client repeatedly
(a client probing above its ceiling); any 5003 or 5011 from the chat role; any 5004 (config unconfirmed beyond the
grace window); any 5102 (malware); P2 with the renderer: 5101 from `Render` to private address classes (connectors
design §7); 5103 on a `public` reference with a personnummer (processing design §9.5); 5001 `user_jwt_invalid` at all (Open WebUI secret mismatch or forgery); P2 (tools design §9.3): any 5012 (definition
changed without a pre-registration), 5013 bursts, any 5015 `level_refused`, 5014 from one tool or document repeatedly
(planted markup).

## 11. Keycloak realm: dev and CI, and the P1 realm additions

### 11.1 Dev and CI realm

`dev/keycloak/ume-realm.json` (the gateway's file name: Keycloak 26's directory import refuses a file whose name is not
`<realm>-realm.json`), realm `ume` (same issuer path shape as the gateway's, `ume-realm.json:2`), adapted
from the gateway's file; passwords and secrets are import placeholders like `${UME_DEV_USER_PASSWORD}`
(`ume-realm.json:27`, `:119`). Extends repo-and-deployment §4.2:

| Element | Content |
|---|---|
| User profile | declares `departmentCodes`, `teamCodes` (Keycloak ≥ 24 drops undeclared attributes, `docs/identity-and-roles.md:148-149`) |
| Realm roles | `rag-platform-admin`, `rag-department-admin`, `rag-pipeline-editor`, `rag-reviewer`, `rag-skill-editor` (P2), `rag-editor`, `rag-viewer`; app permissions `Knowledge.Read.All`, `Chat.Invoke.All` |
| Client scopes | Keycloak's built-in scopes as 26.6.4 creates them for a new realm (`basic`, `profile`, `email`, `roles`, `service_account`, …: an import that lists any `clientScopes` gets none of the built-in ones), then `Chat.Use`, `Knowledge.Read`, `Mcp.Use` (optional client scopes, each with an audience mapper `ume-rag-api`, `include.in.token.scope` on, so they appear in `scope`) |
| Client `ume-rag-admin` | confidential, standard flow only, PKCE S256, redirect and post-logout URIs for `https://localhost:5174` and the adminapi https port (as `ume-realm.json:28-37`); mappers copied: `roles` realm-role mapper (`:41-52`), `departmentCodes` and `teamCodes` attribute mappers (`:54-79`), all to the ID token; **`groups`** group-membership mapper (full path off, ID token on) |
| Client `ume-rag-api-dev` | client credentials only (as `ume-data-dev`, `:84-107`); audience mapper `ume-rag-api`; realm-role mapper to `roles` (access token); service account roles `Knowledge.Read.All`, `Chat.Invoke.All`; `service_account` in its default client scopes (without it a token has no `client_id` claim, to-verify #83) |
| Client `ume-rag-chat-dev` | **new**: public client, code + PKCE, redirect `http://localhost:*` style loopback for test tools; default scope `Chat.Use`, optional `Knowledge.Read`, `Mcp.Use`; for delegated-token and MCP tests |
| Client `open-webui-dev` | confidential, code flow, secret from the placeholder `UME_RAG_OWUI_CLIENT_SECRET` (generated by the AppHost), redirect `http://localhost:8080/oauth/oidc/login/callback` (Open WebUI's default port; §5.3); `email` in ID token; used when `OpenWebUi:Enabled` |
| Groups | `Byggnadsnämnden` (member: `rag-group-member`), `rag-pilot-users` (members: all test users) |
| Users | `rag-platform-admin` (1000, 3000, 5000), `rag-department-admin` (1000), `rag-pipeline-editor` (1000), **`rag-reviewer`** (1000), **`rag-skill-editor`** (1000, P2), `rag-viewer` (1000), `rag-group-member` (no attribute, role `rag-viewer`, since membership never grants a role; via `Byggnadsnämnden` → department 3000), **`rag-no-role`** (1000, no role: every `/api` call 403), **`rag-chat-user`** (no admin role; for delegated chat tokens) |
| Seed | the FakeGateway catalogue (repo-and-deployment §4.3) returns department 3000 with `memberGroups: ["Byggnadsnämnden"]`, so the org sync fills the mirror in dev |

### 11.2 P1: RAG's additions to the shared realm `ume`

The pilot's Keycloak is the gateway's (Decisions table "P1 pilot sign-in"; plan §1): the gateway repo defines the
Compose project, the base realm `ume` (the gateway's clients and roles, without test users) and its operation
(`../gateway-changes/prompt-gateway-session.md` §2). RAG contributes a **realm-additions file** in `deploy/keycloak/`
(owned by stream B, `../archive/p1/README.md` §2; P1 item 16 → hand-off J8, through the user to the gateway session; J11 is the
shared Keycloak with the additions imported and pilot accounts created). It is derived from §11.1 and differs as
follows:

| Element | P1 additions (shared realm) | Differs from §11.1 |
|---|---|---|
| Realm roles | the `rag-*` roles of §1.3 | names unchanged; none collides with the gateway's (§1.6) |
| `departmentCodes` (and `teamCodes`) | **shared with the gateway**: one user attribute, declared in the realm's user profile (`docs/identity-and-roles.md:148-149`), read by both systems' attribute mappers; one value scopes the user in both | the user profile is a realm-level setting, so it is declared once for both systems (who declares it: §16) |
| Client `ume-rag-admin` | confidential, standard flow only, PKCE S256, redirect `https://<rag admin host>/signin-oidc`, post-logout `https://<rag admin host>/signout-callback-oidc`, web origin `https://<rag admin host>` (as the gateway's Keycloak client, `docs/identity-and-roles.md:137-141`); mappers as §11.1 (`roles`, `departmentCodes`, `teamCodes`, `groups`, all to the ID token); secret as an import placeholder | pilot hosts instead of localhost; **full scope allowed off**, with role scope mappings to the `rag-*` roles so `roles` still carries them |
| Client `open-webui` | confidential, code flow, redirect `https://<owui host>/oauth/oidc/login/callback` (§5.3), `email` in the ID token, secret as an import placeholder | named `open-webui` (dev: `open-webui-dev`); pilot host; full scope allowed off |
| Client `ume-rag-api` | the audience of RAG API tokens (`RagApi:Audience`, §7.1); no sign-in flow of its own | dev uses an audience mapper value `ume-rag-api` on `ume-rag-api-dev` and on the scopes `Chat.Use`, `Knowledge.Read`, `Mcp.Use`; whether P1 also carries those scopes, the app permissions `Knowledge.Read.All`/`Chat.Invoke.All` and a client-credentials client is open (§7.1, §16) |
| *Full scope allowed* | **off on every RAG client**, as on the gateway's, so a token carries only its own system's roles | dev keeps Keycloak's default (on) |
| Password policy, brute-force protection, user profile | **in the gateway's base realm file**, not in RAG's additions (Q3); the additions file must not declare `userProfile` (keycloak-config-cli would replace the whole profile) | dev has neither |
| OTP | **not enforced for the pilot** (Decisions "Gateway release 1 and shared Keycloak as built"): password policy and brute-force protection only; MFA comes with brokered Entra ID/AD; G1 records it | dev has none |
| Users, groups | **no test users**; pilot accounts are local Keycloak accounts created by hand (J11, user item U-c); member groups for the pilot department are Keycloak groups whose names a platform admin enters on our department record (§2) | §11.1's users and groups stay dev-only |
| Seed | none: the mirror is filled from the gateway (org sync) and by hand (§2) | – |

**Settled by the gateway session (#390, thekim1/llmgateway#70, 2026-11-06).** Keycloak runs **26.8.1** (the gateway's pin,
`quay.io/keycloak/keycloak:26.8.1-1@sha256:a78d6b79572168cb25886b80a8b5b8d4ed4390743e3eecb810538683e209dd6b`).
**keycloak-config-cli** applies the realm on every `docker compose up` of the gateway's `deploy/keycloak` project: the
gateway's `realm/10-gateway.json` first, then every `*.json` in the operator's additions directory in name order, each
resource type with `no-delete` (a file adds and updates, never removes). RAG's `rag-additions.json` starts with
`"realm": "ume"` and uses `$(env:NAME)` placeholders (`$(env:NAME:-default)` for defaults); the values
(`UME_RAG_ADMIN_PUBLIC_HOST`, `UME_RAG_OPENWEBUI_HOST`, `UME_RAG_OIDC_CLIENT_SECRET`,
`UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`) are in the gateway operator's env file. A RAG change reaches production when the
operator pulls a new tagged RAG version of the file. `rag-realm-settings.json`, the OTP flow and `apply-additions.py`
are not used for the shared realm; a standalone Ume.Rag realm declares password policy, brute-force protection and
the user profile in its own file, `deploy/keycloak/standalone/10-rag-realm.json` (laid out like the gateway's
`realm/`, so it imports before the additions). Local redirect URIs stay out of the production additions directory:
they are in `rag-additions.local.json` (test realms only), which declares the pilot URIs next to the local ones,
since keycloak-config-cli replaces a client's URI lists rather than merging them. Q8:
`ume-rag-api` is the audience client only. Password and brute-force values are the gateway's (the same as RAG's).

**What P1 relies on in the code** (no change needed): the BFF and claim mapping are OIDC-generic
(`src/Ume.Rag.AdminApi/AdminAuthentication.cs:24-56`, `:203-213`), the audit actor falls back to `sub` (§1.1), the tenant and guest checks
are no-ops (§1.7), and `Oidc:Authority`, `Oidc:ClientId`, `Oidc:ClientSecret` point the admin at the shared realm
(§1.4). Locally, the AppHost already points the admin at any external authority when `Oidc:Authority` is set in its
user secrets and then does not start the bundled Keycloak (`src/Ume.Rag.AppHost/Program.cs:45-50`, `:231-238`).

**Later: LDAP federation** to the municipal AD when IT provides a read-only bind account (Decisions table): a user
federation provider in the realm, with an LDAP *user-attribute-mapper* for `departmentCodes` and a *role-ldap-mapper*
or groups for roles (`docs/identity-and-roles.md:143`, `:149-150`). No app change; what happens to the pilot's local
accounts is open (§16).

**Production: AD and Entra ID** (Decisions "Identity providers: AD and Entra ID through Keycloak, Ume.Rag standalone
later"). Both must work, by either route: **brokered** through Keycloak (Entra ID as an OIDC identity provider; AD by
the LDAP federation above or by AD FS) or **directly** through `Oidc:*` (Entra, §12). Brokered accounts bring MFA back
(the OTP row above). The brokered route is tested with a stand-in upstream identity provider (§13.3). Ume.Rag's realm
part needs nothing from the gateway's base realm except the `departmentCodes` and `teamCodes` claims, so Ume.Rag can
run standalone (`deploy/keycloak/standalone/`, §11.3).

**P2: Entra.** The same clients become the Entra registrations of §12 (`ume-rag-admin`, `ume-rag-open-webui`; package 2
for `rag-api`); switching is `Oidc:*`, `RagApi:*` and the Open WebUI OIDC settings.

### 11.3 Shared sign-in with the gateway (2026-11-07, #468)

Decided by the user (Decisions "Shared sign-in with the gateway"): one group membership gives a person their roles in
both products, with **no code in either product referring to the other's roles**.

- **Keycloak (the shared realm `ume`):** operator-made groups carry realm-role mappings for both systems, e.g.
  `ume-admins` → `gateway-admin` + `rag-platform-admin`, `ume-department-admins` → `department-admin` +
  `rag-department-admin`. The RAG-only roles (`rag-pipeline-editor`, `rag-reviewer`, `rag-skill-editor`) are mapped
  to groups the same way. These groups are **operator data** (the admin console or a site file such as
  `additions/90-groups.json`), never in `rag-additions.json`: a RAG file that maps to `gateway-admin` would fail on a
  standalone Keycloak where that role does not exist.
- **One client per system:** `ume-admin` (gateway) and `ume-rag-admin` keep their own role scope mappings, so each
  token carries only its own system's roles in `roles`. Ume.Rag keeps reading `rag-*` roles; the `rag-` rule for role
  groups (§1.4, #413) stays.
- **Direct Entra route:** one app registration per product with app roles named as that product's roles; the same
  Entra security group is assigned to `gateway-admin` on the gateway's app and to `rag-platform-admin` on Ume.Rag's
  (`infra/entra/`). App roles, not the groups claim (object ids, overage above 200 groups).
- **Deployment:** two Compose projects with the same layout and conventions, run side by side or apart; the gateway's
  `deploy/keycloak` project runs Keycloak and Ume.Rag delivers only its additions file; Ume.Rag's default ports avoid
  the gateway's (8443, 9443, 10443, 11443, 8444/9444). Standalone Ume.Rag runs the same Keycloak project with its own
  realm-wide settings and user profile.

## 12. Approval package 1 as Bicep (Microsoft Graph extension)

**Later (P2):** `infra/entra/` and `entra.yml` hold it, not yet applied in a tenant. Entra sign-in is
**P2**: the pilot (P1) signs in with Keycloak (`decisions.md` "P1 pilot sign-in"), so package 1 is
submitted for P2.

### 12.1 Content

Only RAG's own registrations (plan §2); nothing on the gateway's registration changes, and no Graph application
permission is requested in package 1. Every registration, service principal and group gets the deployer as an
**owner**, so the deployer can update it on the next run; Graph says a group created with `Group.Create` and no owner
"isn't modifiable". Both registrations turn implicit grant off explicitly, and both service principals carry the
Enterprise-apps tag (`WindowsAzureActiveDirectoryIntegratedApp`), so the role assignments can be made under
Enterprise applications.

| Object | Change | Needs admin consent / privileged role |
|---|---|---|
| `ume-rag-admin` registration (new) | Web redirect URIs `https://<rag admin host>/signin-oidc`, `/signout-callback-oidc`; `rag-*` app roles (Users/Groups); optional ID-token claims `email`, `acct`; `groupMembershipClaims = SecurityGroup` (§2.5); `acceptMappedClaims` (parameter, default true) for the `departmentCodes` claims-mapping policy, as the gateway's Entra step 4 (plan §2); delegated `openid profile email`; its own client secret, added by an operator, not Bicep (§12.2) | admin consent for the delegated scopes (recommended, avoids prompts) |
| Its service principal | `appRoleAssignmentRequired = true` | no |
| `ume-rag-open-webui` registration (new) | Web redirect `https://<owui host>/oauth/oidc/login/callback` (§5.3); ID-token optional claims `email`, `acct`; delegated Graph `openid profile email` (user consent is enough, but granted tenant-wide by admin to avoid prompts); its own client secret (§12.2) | admin consent for the delegated scopes (recommended) |
| Its service principal | `appRoleAssignmentRequired = true` | no |
| Pilot groups (new) | `Ume.Rag pilot users` (Open WebUI access), `Ume.Rag platform admins`, `Ume.Rag department admins – <pilot department>` (names agreed with IT; no convention exists, plan answered question 5) | `Group.Create` or `Group.ReadWrite.All` for the deployer (open, §12.4) |
| Tool identity and MCP server registrations (**package 2**, P2; not in `infra/entra/` yet) | app registration `ume-rag-tools` with a certificate credential (`keyCredentials`) for client credentials (`/run/certs/ume-rag-tools-entra.pfx`, mounted in `rag-api` and `rag-admin` only, tools design §4.6); per hosted MCP server a registration `ume-mcp-<name>` (first: `ume-mcp-kommun`) with app role `Tools.Invoke` (Application) and its assignment to `ume-rag-tools` (tools design §8.1, §8.3) | assignments need app ownership or `AppRoleAssignment.ReadWrite.All` (manual step, §12.3) |
| App role assignments | pilot users group → `ume-rag-open-webui` (default access); admin groups → `rag-platform-admin` / `rag-department-admin`, reviewer groups per department → `rag-reviewer` (P2: skill editor groups → `rag-skill-editor`) on `ume-rag-admin` | a manual approval step (§12.3) |

### 12.2 Credentials

The Graph Bicep extension cannot create client secrets: `passwordCredentials` appear on `applications` and
`servicePrincipals`, but `secretText` is read-only and Microsoft's limitations page supports only `keyCredentials`
(confirmed in P0, to-verify #84). **Decision (2026-10-29): client secrets, identical to the gateway's setup and code.** Our BFF's secret on
`ume-rag-admin` is created by an operator with `az ad app credential reset --append` and passed to
`init-deployment.sh --oidc-client-secret-file` (`Oidc__ClientSecret`), exactly as in the gateway. Open WebUI's OIDC
client supports only a client secret (`OAUTH_CLIENT_SECRET`), created the same way. Certificates (`private_key_jwt`)
can later be added to **both** systems together (a `gateway-changes` candidate), not to ours alone.

`infra/entra/` owns only RAG's registrations (`ume-rag-admin`, `ume-rag-open-webui`, P2 `rag-*`) and never declares
or touches the gateway's. `appRoles` and `keyCredentials` are arrays a Bicep deployment replaces as a whole, so a
manual portal change to one of our registrations is overwritten on the next run, which the PR plan shows first.

### 12.3 Sketch (`infra/entra/`)

```bicep
// bicepconfig.json: "extensions": { "microsoftGraphV1": "br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0@sha256:<digest of 1.0.0>" }
// (1.0.0 GA, needs Bicep >= 0.36.1; built with Bicep v0.48.1; to-verify #84). Abridged: the real templates are
// main.bicep, admin.bicep and open-webui.bicep, and every object also gets owners: { relationships: [deployer().objectId] }.
extension microsoftGraphV1

param ragAdminHost string
param openWebUiHost string

// App role ids derive from the role value, so they stay the same once assigned.
var ragRoles = [
  { id: guid('ume-rag-admin', 'rag-platform-admin'),   value: 'rag-platform-admin',   displayName: 'RAG platform admin',   allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: everything' }
  { id: guid('ume-rag-admin', 'rag-department-admin'), value: 'rag-department-admin', displayName: 'RAG department admin', allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: own departments' }
  { id: guid('ume-rag-admin', 'rag-pipeline-editor'),  value: 'rag-pipeline-editor',  displayName: 'RAG pipeline editor',  allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: pipelines' }
  { id: guid('ume-rag-admin', 'rag-reviewer'),         value: 'rag-reviewer',         displayName: 'RAG reviewer',         allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: feedback comments and conversations with a reason' }
  { id: guid('ume-rag-admin', 'rag-viewer'),           value: 'rag-viewer',           displayName: 'RAG viewer',           allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: read' }
  { id: guid('ume-rag-admin', 'rag-editor'),           value: 'rag-editor',           displayName: 'RAG editor',           allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: forms in own departments (P2)' }
  { id: guid('ume-rag-admin', 'rag-skill-editor'),     value: 'rag-skill-editor',     displayName: 'RAG skill editor',     allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: skills in own departments (P2)' }
]

resource adminApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'ume-rag-admin'
  displayName: 'Umeå kommun – Ume.Rag admin'
  signInAudience: 'AzureADMyOrg'
  web: { redirectUris: [ 'https://${ragAdminHost}/signin-oidc', 'https://${ragAdminHost}/signout-callback-oidc' ] }
  appRoles: ragRoles
  groupMembershipClaims: 'SecurityGroup'
  optionalClaims: { idToken: [ { name: 'email' }, { name: 'acct' } ] }
}

resource adminSp 'Microsoft.Graph/servicePrincipals@v1.0' = { appId: adminApp.appId, appRoleAssignmentRequired: true }

resource pilotAdmins 'Microsoft.Graph/groups@v1.0' = {
  uniqueName: 'ume-rag-platform-admins', displayName: 'Ume.Rag platform admins'
  mailEnabled: false, mailNickname: 'ume-rag-platform-admins', securityEnabled: true
}

// No appRoleAssignedTo resources: the deployer has no AppRoleAssignment.ReadWrite.All (§12.4), so group → role
// assignments are made by a person after the run (below), like the admin consent.

// open-webui.bicep: application 'ume-rag-open-webui' (web redirect, optional claims), service principal with
// appRoleAssignmentRequired, group 'Ume.Rag pilot users' (its default-access assignment is a manual step too).
```

Not in Bicep: the admin consent for the delegated scopes and the group → app role assignments of §12.1 (admin groups
→ `rag-*` roles, pilot users → `ume-rag-open-webui` default access). They are the approval itself: an Entra administrator
makes them in the portal (or runs the `az rest` commands the workflow prints) after reviewing the run. Group
membership of the pilot groups is maintained by IT, not by IaC.

### 12.4 Workflow (`entra.yml`, repo-and-deployment §8.1)

1. **PR** touching `infra/entra/**`: `bicep build` + `bicep lint`; `azure/login` (OIDC federation, environment
   `entra-plan`, read-only identity with `Application.Read.All`). What-if does **not** support Graph resources
   (Microsoft's Bicep Graph limitations page), so the plan compares `bicep snapshot` (the desired state, resolved
   offline) with the live applications and service principals (`entra.py plan`); groups are listed, not compared,
   because the plan identity cannot read them. Plan as a PR comment.
2. **Dispatch** (`environment: entra-test` / `entra-prod`, required reviewers = the municipality's approvers or a
   change-ticket link): the job builds `main.bicep` and `<env>.bicepparam` to JSON with the pinned Bicep and deploys
   that JSON with `az deployment group create`, not the `.bicepparam` through az's own Bicep. Only
   `sample.bicepparam` is committed; the user adds `test.bicepparam` and `prod.bicepparam` (host names, pilot
   department, no secrets) in a PR, whose plan shows the result. The deployment needs an Azure
   subscription and resource group for the deployment record (Graph Bicep deploys through ARM).
3. **Deployer identity**: a dedicated app with a federated credential for this repository and environment only;
   Graph permissions `Application.ReadWrite.OwnedBy` (it is made an **owner** of every object it creates, §12.1) and
   a group permission; no `AppRoleAssignment.ReadWrite.All` and no directory role, so it cannot grant consent to
   itself or others. **Open question (H7, asked of IT):** which group permission. Option A, `Group.Create` plus
   ownership of the groups it created (this design's choice so far, the narrower grant); whether that is enough for
   an update run is not documented. Option B, `Group.ReadWrite.All`, which Graph's group upsert documents as the least
   privilege (wider: every group in the tenant). The templates work with either; the answer needs a tenant.
4. **After apply**: the job summary lists what still needs a person (`entra.py next-steps`, with the object ids
   filled in): admin consent and group → role assignments (§12.3), client secrets, group membership, the
   `departmentCodes` claims-mapping policy. The ticket id is recorded in P0 checklist item 17.

## 13. Test cases

### 13.1 Copied from the gateway (adapted names)

| Gateway test | Ours | Notes |
|---|---|---|
| `OidcClaimMappingTests` (`tests/Ume.LlmGateway.AdminApi.Tests/OidcClaimMappingTests.cs:13`, `:22`, `:31`, `:43`, `:56`) | `Ume.Rag.AdminApi.Tests/OidcClaimMappingTests` | role values `rag-*` |
| `AdminSessionTests` (`AdminSessionTests.cs:38`, `:57`, `:73`, `:97`) | same | ticket store, 12 h limit; plus a `PostgresAdminSessionStore` test (expired row not returned, sign-out deletes the row) |
| `AuthFailureThrottleTests` (`tests/Ume.LlmGateway.Gateway.Tests/AuthFailureThrottleTests.cs:20`, `:39`, `:54`) | `Ume.Rag.Api.Tests` | unchanged |
| Gateway `SecurityEventTests` (`SecurityEventTests.cs:18`, `:40`, `:66`, `:83`, `:109`) | `Ume.Rag.Api.Tests/AuthFailureTests` | buckets, no presented value, overflow, address truncation |
| `DataApiKeyTests` (`tests/Ume.LlmGateway.DataApi.Tests/DataApiKeyTests.cs:13`, `:38`, `:59`) | `Ume.Rag.Api.Tests/KeyAndTokenTests` | a token can never claim key permissions and the reverse |

### 13.2 Unit (`Ume.Rag.Domain.Tests`, `Ume.Rag.AdminApi.Tests`)

- **Policy matrix** (table-driven, one row per combination): subject kind × client kind × credential × resource level ×
  ceiling × audience kind × membership known/unknown × key scope → expected reason and status. Must include: Open WebUI
  (`internal`) never sees a `sensitive` assistant; MCP client at `public` never sees `internal`; anonymous never sees
  `internal`; `StoresHistory` client refused where the level requires no-history clients; app subject never matches a
  department audience; empty audience list denies everybody; a snapshot older than `Config:MaxUnconfirmedSeconds`
  denies everything with 503, a younger unconfirmed one still decides.
- Residency derivation: `public` → `External`, `internal` → `Eu`, `sensitive` → `OnPrem`, never absent; a
  history-keeping client gets the same level on turn 1 and turn 2; agent run level fixed at the run's start whatever
  the tool results (tools design §5.3).
- Gateway client handler: a request without an explicit ceiling is refused, a key of another level is refused; one
  case per call site (chat, condense, judge, evaluation generation, NER/LLM stages).
- `Filter` returns exactly the resources `Decide` allows (property test over random snapshots); explain access (§4.9)
  returns the reason `Decide` gives for every row of the policy matrix.
- Feature C resolver (§2.8). Tenant and guest checks (`acct=1`, foreign `idp`, `idp` equal to the v1 issuer form
  passes, wrong `tid`; a Keycloak-shaped token without `tid`, `acct`, `idp` passes with `Oidc:TenantId` unset, the P1
  configuration).
- Role resolution: several roles add up; non-`rag-*` roles ignored; no role ⇒ every policy fails. Audit actor `oid`,
  `sub` when there is no `oid` (Keycloak).
- Assistant versions: for every row of the policy matrix, `Decide` gives the same result for two published versions of
  one assistant that differ in instructions, model, collections or skills (within its level); an assistant without a
  published version is `not_found`; a draft's collections never appear in `PolicySnapshot`.
- Owner-team scoping (P2): with `EditOwnerTeamOnly` on, `CanWrite` is true for an owner-team member with `Edit`, false
  for another team's member (403 `owner_team_only`, 6006), true for `Manage`; `teamCodes` matched case-insensitively to
  `Teams.Code`; off ⇒ unchanged.
- Key hasher: both prefixes, wrong prefix for the stored kind, gateway key shape refused, display prefix length.

### 13.3 Integration (`Ume.Rag.Api.Tests`, `Ume.Rag.AdminApi.Tests`, `Ume.Rag.IntegrationTests` with Keycloak and Postgres containers)

- Sign in as each Keycloak user; `/bff/user` shows the expected roles, departments and sources (P0 checklist #9).
- **P1 realm additions** (P1 item 16): the `deploy/keycloak/` file imports into a Keycloak 26.8.1 test container (the gateway's pin, B-15) next
  to a minimal `ume` realm (standing in for the gateway's base realm) without collisions; a sign-in through `ume-rag-admin`
  shows the `rag-*` roles and `departmentCodes` in `/bff/user`, and its ID token carries no gateway role (full scope
  off); the file contains no users.
- **Brokered sign-in** (Decisions "Identity providers"; not built yet): a stand-in upstream identity provider brokered
  by the test realm; a sign-in through it reaches `ume-rag-admin` with `rag-*` roles and `departmentCodes` mapped from
  the upstream claims.
- Token from `ume-rag-api-dev` (client credentials) calls search with `Knowledge.Read.All`; the same token on chat
  without `Chat.Invoke.All` gets 403 `permission_missing`.
- Delegated token from `ume-rag-chat-dev` with `Chat.Use` lists only assistants allowed for client `ume-rag-chat-dev`.
- Key revoked in the admin API stops working on the api within the poll interval (≤ 3 s in the test), with event 5001
  `key_revoked`; key created clears the negative cache.
- Config monitor stopped (Postgres `NOTIFY` and poll blocked) with `Config:MaxUnconfirmedSeconds = 5` ⇒ keyed
  requests keep working and `/health/ready` stays ready for 5 s, then every keyed request is 503 `config_unconfirmed`
  + 5004; resumes when the monitor confirms.
- Open WebUI: real `open-webui` container (nightly e2e) sends the JWT; our API accepts it and stores the conversation
  under (client, `sub`, chat id).

### 13.4 Negative and security tests

| Attack / mistake | Expected |
|---|---|
| JWT with `alg: none`, `alg: RS256` signed with the HS secret as public key, `HS512` | 401 `user_jwt_invalid` |
| JWT signed with another secret; expired; `iat` in the future; lifetime > 600 s; `role: pending`; `iss` ≠ `open-webui` | 401 with the matching reason |
| Valid JWT with an `integration` key, or with no key | 401 / 403 `credential_not_allowed` |
| Plain `X-OpenWebUI-User-Id` header without JWT | 401 `user_jwt_missing` |
| Guessed `X-OpenWebUI-Chat-Id` of another user | new conversation for the caller; the other user's never readable |
| Gateway key `ume-sk-…` sent to our API; our key sent with `Bearer` and `x-api-key` both set | `invalid_key` (nothing derived logged); the `Authorization` value wins as in the gateway |
| 61 unknown keys from one address in a minute | 429 for that address, cached valid keys from it still work, event 5006 |
| `pk` key with a foreign `Origin`, no `Origin`, or for an `internal` assistant | 403 `origin_not_allowed` / 404 |
| Token from another tenant (Keycloak second realm in the test), guest token, unknown `azp` | 401 `wrong_tenant` / `guest_refused` / 403 `client_unknown` |
| User token whose `roles` contains `Knowledge.Read.All` (assigned by mistake) | not an application; `scp` decides |
| Admin cookie replayed after sign-out | 401 (server-side session removed) |
| POST to `/api` without `X-Requested-With` or with a stale CSRF token | 400 |
| Telemetry capture during all of the above | no key, token, JWT, email or display name in logs, traces or events |

## 14. Changes to other documents

Moved to [`../archive/design-history/identity.md`](../archive/design-history/identity.md) on 2026-11-13 (M-14): history, not binding. A citation of §14 or a section under it resolves there.

## 15. Answered questions (2026-10-29, revised 2026-10-30)

Moved to [`../archive/design-history/identity.md`](../archive/design-history/identity.md) on 2026-11-13 (M-14): history, not binding. A citation of §15 or a section under it resolves there.

## 16. To verify (before or during P0/P1)

- Graph Bicep extension: **mostly done** (to-verify #84): version `1.0.0` pinned by digest,
  `passwordCredentials` not writable (§12.2), `members` is a relationship (unused), what-if not supported (§12.4),
  `groupMembershipClaims` writable. Still open, needs a tenant (H7, now P2): the group permission (§12.4 step 3), the
  apply with `Application.ReadWrite.OwnedBy` and the owners the deployer sets on itself, and the plan's
  `GET /applications(uniqueName='…')` (only `PATCH` by `uniqueName` is documented).
- Keycloak: `client_id` claim in client-credentials tokens and `sid` absence (§7.1 step 4): **done** (to-verify #83) on
  26.6.4, with `service_account` in the client's default scopes.
- Entra: `acct` optional claim values and `idp` behaviour for B2B guests in ID and access tokens.
- Open WebUI tag chosen at P0 (V1): **done** (to-verify #46): v0.12.3, JWT fields and fallback as
  designed (`headers.py:41-86`), callback path per §5.3. Still open: an end-to-end request from Open WebUI to
  `rag-api` (needs the chat API, P1).
- **New**: Entra ID tokens of `ume-rag-admin` carry `oid` with the scopes the BFF requests (`openid profile email`).
  Not run in P0: needs the package 1 registration, now P2 (to-verify #151).
- **New**: `groupMembershipClaims = SecurityGroup` puts cloud and synced member groups as object ids into the ID
  token's `groups` claim; how many pilot admins exceed the token's group limit (overage, §2.5).
- **New (P2)**: whether IT can put team codes matching the gateway's `Teams.Code` into a `teamCodes` claim on
  `ume-rag-admin` (the municipality has no department attribute either); otherwise owner-team scoping needs member
  groups on teams (§1.3, §14 item 10).
- **New (P1)**: with *full scope allowed* off, the `roles` mapper of `ume-rag-admin` still emits the `rag-*` roles once
  they are in the client's role scope mappings, and no gateway role (§11.2; P1 item 16's sign-in test).
- **New (P1)**: Keycloak's partial import covers users, clients, groups and roles, but not, as far as the admin console
  shows, client scopes, the user profile, password and brute-force policy or authentication flows. Settled: the shared realm uses keycloak-config-cli instead
  (open question 2 below; Decisions "Gateway release 1 and shared Keycloak as built").

**Answered for P1 (#390, 2026-11-06):** Q1 and Q6: the gateway operator holds realm admin for the pilot and demo
(accounts, role grants, `departmentCodes`/`teamCodes`, resets; runbook in the gateway's `docs/keycloak.md`); brokered
organisations manage accounts in their own directory. Q2: keycloak-config-cli, `no-delete`, name order (§11.2). Q3:
realm-wide settings in the gateway's base file. Q4: OTP not enforced for the pilot (Decisions "Gateway release 1 and
shared Keycloak as built"). Q8: audience only. The list below keeps the original questions; 5, 7 and 9 are unchanged.

**Open for P1 (shared Keycloak; not decided by the 2026-10-30 decision, 2026-10-31).** Each needs an answer before
J11; the gateway session (`../gateway-changes/prompt-gateway-session.md` §2) or the user decides, not the streams.

1. **Who administers the shared realm** in production: accounts, role grants, `departmentCodes`, OTP resets, the
   realm's admin users and their own OTP. The gateway session writes the runbook; who holds the Keycloak admin role
   for realm `ume` (the gateway's operators, RAG's platform admins, or both, with which Keycloak admin roles) is open.
2. **How RAG's additions are imported and kept in step** with the gateway's base realm: an import job, `kcadm`, or a
   documented procedure (the gateway session chooses and says which); how a later change to `deploy/keycloak/**` (a new
   redirect URI, a new `rag-*` role) reaches production, who applies it, and how a mistake in one system's part is
   kept from breaking the other's sign-in.
3. **Realm-level settings shared by both systems**: password policy, brute-force protection, the user profile
   (`departmentCodes`, `teamCodes`) and the OTP flow apply to the whole realm, not per client. P1 item 16 puts them in
   RAG's file (provisionally, §11.2); whether they belong in the gateway's base realm instead.
4. **OTP for admins**: which accounts count as admins (every account with a `rag-*` role, only
   `rag-platform-admin`/`rag-department-admin`, and the gateway's `gateway-admin`), and how it is enforced (the
   *Configure OTP* required action per account, or a conditional browser flow on role). Provisionally (§11.2):
   `rag-platform-admin` and `rag-department-admin`, by a conditional browser flow on role.
5. **Who may sign in to Open WebUI** (§5.3): any account in the realm today; a role gate or a flow condition.
6. **Account lifecycle**: who creates, disables and removes pilot accounts (leavers), how initial credentials and OTP
   enrolment reach people, and who keeps `departmentCodes` and member-group membership current.
7. **Identity continuity** when LDAP federation arrives and when P2 moves to Entra: the Keycloak `sub` of a local
   account may differ from the federated one, and Entra's `oid` differs from both. Audit actors change (§1.1); Open
   WebUI accounts appear to be keyed by the provider's `sub` with `OAUTH_MERGE_ACCOUNTS_BY_EMAIL=false`, so a switch may
   create new Open WebUI users and new `SubjectId`s for our conversations (to verify). Whether the pilot's accounts and
   chats are migrated or start over.
8. **Token-caller parts in the P1 additions**: whether `ume-rag-api` in P1 is only the audience, or also brings the
   `Chat.Use`/`Knowledge.Read`/`Mcp.Use` scopes, the app permissions `Knowledge.Read.All`/`Chat.Invoke.All` and a
   client-credentials client (no P1 caller needs them, §7.1). Provisionally (§11.2): the audience client only.
9. **E1 spike** (§9.4, weakness review §5): answered by the Decisions table "P1b briefs" (O3): E1 moves to P2 with
   Entra.

## 17. Deferred or removed (simplification pass)

Moved to [`../archive/design-history/identity.md`](../archive/design-history/identity.md) on 2026-11-13 (M-14): history, not binding. A citation of §17 or a section under it resolves there.

## 18. Deferred or removed (weakness review)

Moved to [`../archive/design-history/identity.md`](../archive/design-history/identity.md) on 2026-11-13 (M-14): history, not binding. A citation of §18 or a section under it resolves there.

## 19. Setup mode and the sign-in settings (2026-11-15, Decisions "Setup wizard")

The reasons and the alternatives are in [`../proposals/setup-wizard.md`](../proposals/setup-wizard.md); the rules:

**Effective sign-in values.** rag-admin's sign-in values (`Authority`, `ClientId`, `ClientSecret`, the role,
department, team and group claims, the extra scopes, `TenantId`, `RefuseGuests`) each come from the deployment
(`Oidc:*` in the raw configuration, an empty value is unset), else from `Settings.Identity.Oidc.*`, else from the
code's default. The API reports each value's source (`deployment`, `wizard`, `default`) and never a secret (it answers
*set* and when). `Oidc:RoleGroups:rag-platform-admin` stays a deployment value (§2.5). rag-admin starts without an
authority.

**Stored secrets.** The client secrets are encrypted with Data Protection under a purpose per secret
(`config."DataProtectionKeys"`, the ring protected by `data-protection.pfx`). rag-api registers Data Protection with
the same application name and certificate and with key generation off; its code unprotects only Open WebUI's secret.
The purposes keep one secret's ciphertext from being read as another's; they are not access control (rag-api's role
can read the settings row and the service holds the certificate). The worker has no certificate.

**Setup state.** `SetupCompletedAt` in the settings row. Setup is open when it is null, or when `Setup:Reopen` holds
a value other than the last consumed one; that value is consumed when setup opens. An opening lasts one hour. A start
with an authority and a client secret from the deployment sets `SetupCompletedAt` (the client id has a default, so it
can't tell a declared deployment; a deployment that gives only the authority, as the shared Portainer stack does, gets
the wizard for the secret and the test; one with a public client and no secret gets the wizard once). Nothing else opens setup: a missing
row (`rag-migrate` seeds it, so the first run has a row with `SetupCompletedAt` null), an unreadable secret or a
missing value leaves it closed, and rag-admin logs the reason. Setup sessions and tests are deleted when their opening
ends.

**Setup code.** HMAC-SHA-256 of the setup secret file (`Setup__Secret`, generated by the deployment like every
generated secret, never overwritten) over the opening's nonce (in the settings row), shown as 128 bits in groups.
rag-admin writes it to standard output at start while setup is open, with the wizard's address, never through the
logger or a security event. A wrong code never voids the code and never stops the right one for longer than a minute:
each instance checks at most 20 codes a minute (then 429 with `Retry-After`), on top of the per-address limit; nothing
about wrong codes is stored, and their security event is sampled (one per 10 seconds, with a count). 128 bits need no
hard stop, and a hard stop would let anyone who reaches the page shut the deployer out.

**Setup API** (`/bff/setup/*`, 404 while setup is closed): `POST session` (the code → a setup cookie, 30 minutes,
with the BFF's antiforgery), `GET state` (the values with their sources, the host names, the redirect URIs),
`POST test` and its callback, `POST complete`.

**Test sign-in.** `POST test` takes candidate values, fetches the discovery document (`https` only outside
development, no redirects, no response content returned) and starts an authorization-code sign-in with them and
rag-admin's redirect URI. The candidate values are stored encrypted with the setup session. The callback runs the
sign-in checks of §1.3 and §2 and stores the result: subject, name, roles, groups, departments, and whether the person
is a platform admin. `POST complete` saves the tested values only when the latest test passed, was made with exactly
those values, and its person is a platform admin; it sets `SetupCompletedAt`, clears the nonce and ends the setup
session.

**Whose secret goes where.** A test sends a client secret only to the provider it belongs to: the wizard's stored
secret only when the candidate's authority and client id are the ones in force (else `secret_required`: type it
again), the deployment's secret only when the deployment sets the authority too (else
`deployment_secret_without_authority`). Both are checked again at the callback, before the token call.

**The test belongs to one browser.** `POST test` sets a cookie on the callback path (`HttpOnly`, `Secure`,
`SameSite=None`, since the provider's answer is a cross-site form post) whose hash is in the test row; a callback
without it fails as `browser_mismatch` and makes no token call. So a test can't be finished by sending its address to
someone else.

**Changing sign-in later.** No route of an admin session writes `Settings.Identity.Oidc.*` or
`Settings.OpenWebUi.Oidc.*` (the provider's display name excepted). They change in setup mode after a re-open, during
which the existing sign-in keeps working.

**Reload.** rag-admin builds its `OpenIdConnectOptions` from the effective values through one configure step. Before
authentication runs on `/bff/login`, `/bff/user`, `/bff/logout` and the callback it reads the effective values (one
row) and compares a SHA-256 fingerprint of them, the secret included, with the one the live options were built from;
when they differ the options are evicted and built again. The fingerprint, not the settings version, is compared, so a
reloaded secret file is followed and an unrelated settings save rebuilds nothing; it is never logged. Every sign-in
starts at `/bff/login`: a challenge from another route is redirected there (`/api` answers 401). Existing sessions stay
valid; replicas share nothing and each follows at its own next sign-in. If the row can't be read, the last values stay
in use. Without a usable authority (none, not an absolute https URL, or a stored secret that can't be read)
`/bff/user` answers `signIn.configured: false` and a sign-in is refused with `sign_in_not_configured` (409). A callback
that can't be completed gives no session, security event 5001 `callback_failed` and a redirect to `/?signin=failed`.
Built in M-20 S2 (#762).

**How the test sign-in runs** (S2's spike, read in the framework's source): not a second OpenID Connect scheme (two
handlers share a callback path only with `SkipUnrecognizedRequests`, and a scheme's options are per process while
candidate values belong to a setup session and the callback may land on another replica). The test is a hand-written
authorization-code exchange: `state` is `rag-setup-test.<id>` (the handler's own state is base64url and never holds a
dot), a middleware before authentication takes callbacks with that marker and passes every other one on untouched; the
test row (single use, expiring) holds the PKCE verifier and nonce; the id token is validated with
`Microsoft.IdentityModel` against the candidate's metadata (issuer, audience, signature, lifetime, nonce), and then the
same `ValidateSignInAsync` as a real sign-in runs on the candidate values. A provider whose metadata requires pushed
authorization requests is refused with a message.

**Open WebUI.** `Settings.OpenWebUi.Oidc.*`: the provider URL (the discovery document's address; default rag-admin's
effective authority + `/.well-known/openid-configuration`), the client id (default `open-webui`), the provider's
display name, the client secret (encrypted, its own purpose) and its optional expiry date. A deployment value
(`OpenWebUi:Oidc:*`) wins. They are set in the wizard's Open WebUI step (`target: "open-webui"` on `POST test` and
`POST complete`), shown only when the deployment runs Open WebUI (`OpenWebUi:Enabled`), with Open WebUI's redirect URI
to register.

- **Its default provider inside a setup session:** on a first run the admin's authority is tested but not saved when
  this step comes (it is saved at Finish). So while rag-admin has no effective authority, the default provider URL of
  this target follows the authority of the session's latest passed rag-admin test: the field can stay empty, the check
  uses that provider, and null is saved, so the setting follows the admin's provider afterwards. Outside a setup
  session nothing changes. A deployment secret is still sent only to a provider the deployment names.
- **Its test is a back-channel check, not a sign-in:** the provider sends the browser back to Open WebUI, so rag-admin
  never sees a callback. The test fetches the discovery document (the provider URL must be the issuer's own) and the
  keys, then makes one token request with a random code and the candidate client: `invalid_grant` means the client was
  accepted, `invalid_client` or any 401 that it was refused, anything else that it could not be verified (saving is
  allowed, with a warning). It can't tell whether Open WebUI's redirect URI is registered. The secret rules above apply;
  a deployment secret also goes to the default derived from the deployment's own authority.
- **Saving** needs a session whose latest rag-admin test passed as a platform admin (that person is the audit's actor),
  a test with discovery ok and the client not refused, and the same values; it doesn't close setup (event 6013).
- **rag-api** serves the effective values on `GET /internal/openwebui/sign-in` to the managed Open WebUI client only
  (§5): 401 without a key, 403 for any other client, `no-store`, not in the API reference. Its `version` is an HMAC of
  the values under a deployment key, so it changes with any value and can't be used to test guesses. The Portainer
  proxy answers 404 for `/internal/`; in Compose Open WebUI calls rag-api by its public address, so only the key opens
  it there.
- **Open WebUI's start wrapper** (`deploy/openwebui/start-with-sign-in.py`) fetches them before it starts, starts
  without OIDC sign-in when there are none (a public client gets PKCE instead of a secret), asks again every minute
  and exits when the answer changed, so the restart policy starts Open WebUI with the new values. A failed call changes
  nothing.
- The provider's display name alone is an ordinary setting (`PUT /api/settings/openwebui`).

Built in M-20 S5 (#771).

**Security events:** setup opened, setup code refused, test sign-in (result), setup completed, each with the address;
`docs/security-events.md` has the numbers. The audit row of a completion names the tested person.
