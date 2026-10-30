# Identity, authentication and authorisation: detailed design

Detail step 3 of the Ume.Rag design. Details [`plan.md`](plan.md) (binding, incl. its answered questions) and the
Decisions table in [`../../01-general-idea.md`](../../01-general-idea.md) §10. Consistent with
[`../platform/data-model.md`](../platform/data-model.md) (step 1) and
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) (step 2); deviations are listed in §14.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5); Open WebUI citations are in
`~/git/rag-platforms/open-webui`. "Copy" means the file is copied with namespace and names changed only; "adapt" means
copied and changed as stated; "new" means there is no gateway counterpart.

**What is copied from the gateway**

| Area | Gateway source | Here |
|---|---|---|
| OIDC code + PKCE BFF, cookie session in Redis, CSRF, 8 h / 12 h sessions | `src/Ume.LlmGateway.AdminApi/AdminAuthentication.cs:71-233`, `AdminSessions.cs`, ADR 002 | §1 (adapt) |
| Claim mapping (`roles`, `departmentCodes`, `teamCodes`, group → role) | `AdminAuthentication.cs:18-65`, `AdminOptions.cs:107-131` | §1.3 (copy) |
| Department scope from `departmentCodes` matched on cost-centre code | `AdminContext.cs:81-90`, `docs/identity-and-roles.md:26-31` | §1.3, §2 (copy + feature C) |
| Virtual keys: HMAC + pepper, cache incl. negative cache, failure buckets, per-address throttle, 24 h grace, audited reveal | `Domain/Services/VirtualKeyHasher.cs`, `Gateway/Pipeline/KeyAuthenticator.cs`, `AuthFailureRecorder.cs`, `AuthFailureThrottle.cs`, `Domain/Services/KeyRotation.cs`, `AdminApi/KeyEndpoints.cs:79-133` | §6 (adapt) |
| Machine tokens: `Authority`/`Audience`/`PermissionClaims`, keys vs tokens never mixed | `DataApi/DataApiAuthentication.cs`, `DataApiOptions.cs` | §7 (adapt) |
| Security events: one category, stable names and ids, `Printable()`, no secrets | `Infrastructure/Security/SecurityEvents.cs`, `docs/data-access.md:54-133` | §10 (copy pattern) |
| Keycloak dev realm | `dev/keycloak/ume-realm.json` | §11 (adapt) |

## 1. Admin sign-in (BFF)

### 1.1 Mechanism (unchanged from the gateway)

- Cookie scheme as default, OIDC as challenge scheme (`AdminAuthentication.cs:82-86`); code flow with PKCE,
  `SaveTokens = false`, `MapInboundClaims = false`, name claim `name`, role claim `roles`, scope `email` added
  (`:113-122`). No token ever reaches the browser (ADR 002, `docs/adr.md:6`).
- Claim mapping runs in `OnTokenValidated` (`:123-127`); feature C (§2) and the tenant/guest check (§1.7) run there
  too, right after it.
- Calls to `/api/*` without a session answer 401 instead of redirecting (`:111`, `:130-138`); 403 for missing rights
  (`:112`). Sign-out returns the IdP's end-session URL as JSON (`:139-149`).
- Server-side sessions: the cookie holds only a random id; the ticket is Data-Protection-encrypted in Redis
  (`AdminSessions.cs:16-54`), key `ume-rag:admin-session:{id}` (data-model §11).
- Lifetime 8 h, no sliding (`AdminAuthentication.cs:93-94`, `AdminSessions.cs:64`); `/bff/session/extend` moves
  expiry by up to 8 h but never past `Admin:MaxSessionHours` (12) from sign-in (`AdminSessions.cs:87-112`,
  `AdminAuthentication.cs:217-232`, `AdminOptions.cs:19-20`). Roles and departments are read at sign-in, so IdP
  changes apply at the latest after 12 h or at the next sign-in (`docs/identity-and-roles.md:79-83`).
- CSRF: antiforgery header `X-XSRF-TOKEN`, a readable token cookie set by `/bff/user`, and `X-Requested-With:
  XMLHttpRequest` required on every non-GET to `/api` and `/bff` (`AdminAuthentication.cs:74-81`, `:190-194`,
  `:281-305`).
- A random session id claim `ume_session` is added at first sign-in only (not on extension) and goes into audit
  entries and security events (`:100-107`, `:255-256`).
- Audit actor = `sub`, else name (`AdminContext.cs:76-79`); actor context (name, roles, departments, address,
  session, request id, user agent) from `AdminActor` (`AdminActivity.cs:18-57`).

### 1.2 Class map

| Gateway (`Ume.LlmGateway.AdminApi`) | Ume.Rag (`Ume.Rag.AdminApi`) | How |
|---|---|---|
| `AdminAuthentication.ApplyClaimMapping`, `CopyClaims` (`AdminAuthentication.cs:18-65`) | same | copy |
| `AdminAuthentication.AddAdminAuthentication` (`:71-174`) | same | adapt: cookie names (below), policies for `rag-*` roles, `OnTokenValidated` adds §1.7 and §2.3 |
| `AdminAuthentication.MapAdminAuthentication` (`:176-233`) | same | adapt: `/bff/user` adds `departmentSources` and `departmentLookup` (§1.5) |
| `AdminRoles` (`:237-245`) | `RagAdminRoles` | adapt: values in §1.3 |
| `AdminClaims` (`:248-257`) | same | copy (`sub`, `roles`, `departmentCodes`, `teamCodes`, `ume_session`) |
| `AdminPolicies` (`:260-279`) | same | adapt: §1.3 |
| `AdminAntiforgeryMiddleware` (`:281-305`) | same | copy |
| `OidcOptions` (`AdminOptions.cs:107-131`) | same | adapt: default `ClientId` `ume-rag-admin`; new `GroupLookup`, `TenantId`, `RefuseGuests` (§1.4) |
| `AdminOptions` (`AdminOptions.cs:7-51`) | same | adapt: keep `RequestsPerMinute`, `MaxSessionHours`, `AuditSourceAddress`; drop price/release settings |
| `AdminTicketStore`, `AdminSessionLimit` (`AdminSessions.cs`) | same | copy; protector purpose `Ume.Rag.AdminApi.Session.v1` |
| `AdminActor`, `AdminActivityMiddleware`, `AccessDeniedThrottle`, `AdminClientAddresses` (`AdminActivity.cs`) | same | copy |
| `AdminContext` scope methods (`AdminContext.cs:69-130`) | `AdminContext` | adapt: `IsAdmin` = `rag-platform-admin`; `Departments()` unchanged; teams/keys per RAG roles |
| – | `DepartmentMembershipResolver`, `GraphGroupClient` | new (§2) |

Cookie names must differ from the gateway's: browsers ignore the port, so on `localhost` the gateway admin (5173)
and ours (5174) would overwrite each other's `__Host-ume-admin` and `XSRF-TOKEN`. Ours: `__Host-ume-rag-admin`
(session), `__Host-ume-rag-csrf` (antiforgery), `RAG-XSRF-TOKEN` (readable token; the copied admin UI's fetch helper
reads this name).

### 1.3 Claim mapping and role resolution

`ApplyClaimMapping` is copied unchanged: it expands JSON-array claims for the role, department, team and group claims
(`AdminAuthentication.cs:24-36`), copies differently named claims to `roles`/`departmentCodes`/`teamCodes`
(`:37-39`, `:51-65`) and adds a role for each `Oidc:RoleGroups:<role>` entry whose `;`-separated, case-insensitive
groups intersect the group claim (`:40-48`).

| Role value | Policy membership | Scope |
|---|---|---|
| `rag-platform-admin` | `Admin`, `Manage`, `EditPipelines`, `Edit`, `Read` | every department (like `gateway-admin`, `AdminContext.cs:81-86`) |
| `rag-department-admin` | `Manage`, `EditPipelines`, `Edit`, `Read` | `departmentCodes` (+ feature C) |
| `rag-pipeline-editor` | `EditPipelines`, `Read` | same |
| `rag-editor` (P2) | `Edit`, `Read` | same |
| `rag-viewer` | `Read` | same |
| none | nothing: every `/api` call is 403 (the gateway allows the catalogue to role-less users, `docs/identity-and-roles.md:18`; we have no public page) | – |

As in the gateway, policies are `RequireRole(any of …)` (`AdminAuthentication.cs:151-158`), so several roles add up
and the highest one counts (`docs/identity-and-roles.md:22-24`). Role values that are not `rag-*` are ignored, so a
token from the shared registration carrying `gateway-admin` grants nothing here, and the gateway ignores `rag-*`
because its policies name only its own roles. `teamCodes` is read and shown but grants nothing in P1 (no team-scoped
role yet, plan §3).

### 1.4 Settings

Same names and semantics as the gateway (`docs/identity-and-roles.md:106-121`, `deploy/compose.prod.yaml:136-146`)
with the `UME_RAG_` prefix; for local runs the same keys as `Oidc:<Setting>` in AppHost user secrets.

| Env (`deploy/.env`) | Setting | Default | Notes |
|---|---|---|---|
| `UME_RAG_OIDC_AUTHORITY` | `Oidc:Authority` | – (required) | Entra: `https://login.microsoftonline.com/<tid>/v2.0`; shared registration: same value as the gateway's |
| `UME_RAG_OIDC_CLIENT_ID` | `Oidc:ClientId` | `ume-rag-admin` | shared registration: the gateway's client id (`init-deployment.sh --oidc-client-id`, repo-and-deployment §7.1) |
| (secret file) `Oidc__ClientSecret` | `Oidc:ClientSecret` | – | our **own** secret on the shared registration (§12.2), never the gateway's; `init-deployment.sh --oidc-client-secret-file` as in the gateway |
| `UME_RAG_OIDC_ROLE_CLAIM` | `Oidc:RoleClaim` | `roles` | |
| `UME_RAG_OIDC_DEPARTMENT_CLAIM` | `Oidc:DepartmentClaim` | `departmentCodes` | |
| `UME_RAG_OIDC_TEAM_CLAIM` | `Oidc:TeamClaim` | `teamCodes` | |
| `UME_RAG_OIDC_GROUP_CLAIM` | `Oidc:GroupClaim` | `groups` | |
| `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` | `Oidc:RoleGroups:rag-platform-admin` | – | `;`-separated, case-insensitive |
| `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN` | `Oidc:RoleGroups:rag-department-admin` | – | |
| `UME_RAG_OIDC_GROUPS_PIPELINE_EDITOR` | `Oidc:RoleGroups:rag-pipeline-editor` | – | |
| `UME_RAG_OIDC_GROUPS_EDITOR` | `Oidc:RoleGroups:rag-editor` | – | P2 |
| `UME_RAG_OIDC_GROUPS_VIEWER` | `Oidc:RoleGroups:rag-viewer` | – | |
| `UME_RAG_OIDC_EXTRA_SCOPE` | `Oidc:ExtraScopes:0` | `email` | |
| `UME_RAG_OIDC_GROUP_LOOKUP` | `Oidc:GroupLookup` | `Claims` | **new (feature C, both systems)**: `Claims` or `Graph` (§2.4) |
| `UME_RAG_OIDC_TENANT_ID` | `Oidc:TenantId` | – | **new**: when set, `tid` must equal it (§1.7) |
| `UME_RAG_OIDC_REFUSE_GUESTS` | `Oidc:RefuseGuests` | `true` | **new** (§1.7) |
| – | `Admin:MaxSessionHours` | 12 | copy (`AdminOptions.cs:19-20`) |
| – | `Admin:RequestsPerMinute` | 120 | copy (`AdminOptions.cs:12-13`) |
| – | `Admin:AuditSourceAddress` | `Full` | copy (`AdminOptions.cs:26`) |

### 1.5 `/bff/user`

Copied (`AdminAuthentication.cs:188-208`): `isAuthenticated`, `environment`, `name`, `email`, `roles`,
`departmentCodes`, `teamCodes`, `sessionExpiresAt`, `sessionLimitAt`. Two additions, proposed for the gateway too
(feature C, §2.7): `departmentSources` (`[{ code, source: "claim" | "group" }]`) and `departmentLookup`
(`"claims"`, `"graph"` or `"failed"`), so the setup check in `docs/identity-and-roles.md:217-229` also shows where a
department came from and whether the Graph lookup worked. No group ids are returned.

### 1.6 Shared gateway registration (decided) vs twin (fallback)

**Shared** (plan §2, answered question 7): our BFF uses the gateway's existing admin registration with its **own
credential** (a client secret of its own, decided 2026-10-29; §12.2). Same authority and client id in both `.env` files; redirect URIs of both hosts on
the registration; app roles of both systems on it; users and groups assigned once.

Consequences, accepted:

- The ID token carries both systems' roles; each ignores the other's (§1.3).
- A user assigned only a `rag-*` role can sign in to the gateway admin and see its catalogue (the gateway gives
  role-less users the catalogue, `docs/identity-and-roles.md:18`). The catalogue lists models and the base URL, which
  every key holder sees anyway. The reverse is harmless: a gateway-only user sees nothing here (§1.3).
- *Assignment required* on the enterprise application covers both systems. Assigning department member groups to the
  application is not needed (§2.4).
- Revoking our credential never affects the gateway's, and the reverse.

**Twin** (only if the gateway admin is not on Entra yet or IT refuses the change): registration `ume-rag-admin`
configured exactly like the gateway's (`docs/identity-and-roles.md:160-182`) plus §12's additions. Nothing in the code
differs; only `UME_RAG_OIDC_CLIENT_ID` and the Bicep module (§12.3) change. **Development and CI always use the twin
model** (Keycloak client `ume-rag-admin`, §11), so both stacks run side by side.

### 1.7 Tenant and guests (new, both systems)

Run in `OnTokenValidated` before claim mapping; a refusal ends the OIDC callback with a redirect to an explanatory
page (no session; same in both systems, gateway design §4.6) and event 5001 (`Reason`
`wrong_tenant` / `guest_refused`):

- `Oidc:TenantId` set ⇒ `tid` must equal it. (The v2 issuer already contains the tenant; this also covers a
  misconfigured `common` authority.)
- `Oidc:RefuseGuests` ⇒ refuse when the optional claim `acct` is `1` (guest), or when an `idp` claim is present and
  differs from the issuer (Entra sets `idp` for users from another tenant or IdP). `acct` is requested as an optional
  ID-token claim on the registration (§12.3). Keycloak and AD FS emit neither claim, so the check is a no-op there.

The gateway has neither check today; with a shared registration a guest assigned by mistake would reach the gateway
admin but not ours. Added to `gateway-changes` as a small item next to feature C (§14).

## 2. Feature C: department scope from member groups (both systems)

### 2.1 Where member groups live

- **Gateway (source of truth)**: `Department` (`Domain/Entities/Entities.cs:8-19`) gains `MemberGroups`
  (`varchar(256)[]`, default empty), edited by gateway admins on the *Organisation* page; every change is an audit
  entry (`admin.change`).
- **Data API**: `GET /v1/catalog/departments` (`DataApi/DataEndpoints.cs:140`, permission `catalog.read`,
  `docs/data-access.md:162`) gains `memberGroups: string[]`.
- **Ume.Rag (mirror)**: `config."Departments"."MemberGroups"` (data-model §2.1). The admin API's `OrgSync` job reads
  `/v1/catalog/departments` and `/teams` every **5 minutes** and on demand (*Sync now* on our organisation page),
  with a gateway **Data API key** (`ume-dk-…`, permission `catalog.read` only; secret file
  `secrets/admin/OrgSync__DataApiKey`). A key needs no IdP registration, which keeps the "no registration before the
  pilot" rule (plan §2). Only changed rows are written, so an unchanged sync does not bump the config version
  (data-model §2.1). Member groups are never edited in our admin UI; it links to the gateway's page.

**Freshness**: a change in the gateway reaches our mirror within 5 minutes (or at once with *Sync now*), and a
signed-in admin's scope at their next sign-in (scopes are fixed at sign-in, §1.1). If the sync fails, the last mirror
stays in use (it is never wider than the gateway was at that time); after 3 failed runs a warning shows on the
*Gateway* page and in health (`degraded`). If the mirror is older than `OrgSync:MaxAgeHours` (24), group-derived
department scope is switched off in both places (§2.6), because a removal in the gateway would otherwise never arrive.

### 2.2 Group values: claim values, not only object ids (decision)

The plan stores groups "by object id" for Entra. The gateway is open source and also documents Keycloak and AD FS,
where the group claim holds **names** (`docs/identity-and-roles.md:116`, `:146-147`, `:199-215`), and its existing
`Oidc:RoleGroups` already matches claim values case-insensitively (`AdminAuthentication.cs:40-48`). So a member group
is **a value of the group claim**: an Entra object id, an AD FS/Keycloak group name. Matching is case-insensitive
like `RoleGroups`. On Entra this is exactly the plan's "object id" rule; on Keycloak the dev realm can use a plain
group mapper (repo-and-deployment §4.2). Graph lookups (§2.4) only ever consider values that parse as GUIDs.
Data-model column type changes accordingly (§14).

### 2.3 Sign-in resolution order

In `OnTokenValidated`, after `ApplyClaimMapping` (identical in both systems; `DepartmentMembershipResolver`):

1. Codes from the `departmentCodes` claim (after mapping) → `departmentSources` `claim`.
2. `candidates` = member groups of every **active** department in the mirror (gateway: its own table) whose
   cost-centre code is not already in step 1.
3. `known` = the group claim values (`Oidc:GroupClaim`) ∩ `candidates`.
4. If `Oidc:GroupLookup = Graph` and the user has an `oid`: for GUID-shaped `candidates` not in `known`, ask Graph
   (§2.4); add the ones it confirms. Overage (§2.4) is handled the same way, since then `known` is empty.
5. For each department with at least one member group in `known`: add a `departmentCodes` claim with its cost-centre
   code → source `group`.
6. Event `admin.signin` / `rag.admin.signin` gets the extra attribute `DepartmentLookup` (§10).

Because the result is written as ordinary `departmentCodes` claims, every scoped query (`AdminContext.Departments()`,
`AdminContext.cs:81-90`) works unchanged in both systems, and `/bff/user` shows the result. Roles still come only from
`roles` or `Oidc:RoleGroups` (plan §3): membership never grants a role.

### 2.4 Graph lookup (Entra)

- **Why not only the claim.** With `groupMembershipClaims = ApplicationGroup` (the gateway's recommendation is app
  roles, `docs/identity-and-roles.md:184-185`), Entra emits only groups **assigned to the application**, so a
  department member group is in the claim only if someone also assigned it to the admin application, which would let
  the whole department sign in. We therefore do not assign member groups and ask Graph for whatever the claim does
  not show. Admin sign-ins are rare, so this costs about one Graph call per sign-in.
- **Overage**: when a user is in too many groups, Entra omits `groups` and sets `_claim_names.groups`; step 4 covers
  it without special handling. We never follow `_claim_sources` (it points at the old AAD Graph).
- **Call**: `POST https://graph.microsoft.com/v1.0/users/{oid}/checkMemberGroups` with up to 20 `groupIds` per call
  (chunked), application permission `GroupMember.Read.All` (package 1; whether `User.ReadBasic.All` is also needed for
  the `/users/{id}` path is *to verify*, §12.3). It is transitive, which matches how departments nest groups.
  Token by client credentials with the same registration and our own credential, through the gateway's small
  `GraphTokenClient` copied as is (no MSAL; token cached until 5 minutes before expiry; gateway design §4.4).
  `GroupLookup = Graph` requires `Oidc:TenantId` (validated at start).
- **Cache**: Redis `ume-rag:groups:{oid}` (data-model §11), 10 minutes, value `{ checked: [ids], member: [ids] }`.
  A later lookup only asks Graph for ids not in `checked` (a new member group added to config is checked at once). The
  gateway uses its own prefix (`ume:groups:{oid}`; the gateway's `RedisKeys.cs` decides the exact name).
- **Timeouts**: 3 s per call, one retry, Polly circuit (copied `AddStandardResilienceHandler` from ServiceDefaults).
- **Display names** of groups (admin UI, gateway Organisation page, P2 audience picker): `GET /groups/{id}` and
  `GET /groups?$search="displayName:…"` (header `ConsistencyLevel: eventual`), same permission.

### 2.5 Keycloak and AD FS

`Oidc:GroupLookup = Claims` (default). Keycloak: a *Group Membership* mapper (claim `groups`, *Full group path* off,
*Add to ID token* on, `docs/identity-and-roles.md:145-147`) and member groups entered as names. AD FS: group claim
`group` with unqualified names (`:199-215`). No overage and no Graph.

### 2.6 Failure behaviour (decision: claims-only, never fail open)

| Situation | Admin sign-in (both systems) | Data plane (P2 department/group audiences, §4.3) |
|---|---|---|
| Graph error, timeout or circuit open | Sign-in **succeeds** with step 1 + 3 only; `departmentLookup: "failed"`; banner "Avdelningar från grupper kunde inte hämtas"; event `rag.identity.lookup_failed` (5007); result not cached | Decision needs membership that is unknown ⇒ **503 `membership_unavailable`** (not 403), event 5007; requests whose access does not depend on groups are unaffected |
| Mirror older than `OrgSync:MaxAgeHours` (RAG only) | steps 2–5 skipped, `departmentLookup: "failed"` | department audiences deny with 503 `membership_unavailable` |
| User has no `oid` (Keycloak, AD FS) | Graph skipped (`departmentLookup: "claims"`) | audiences evaluated from claims only |

Justification: failing the whole sign-in would lock out platform admins (who need no departments) whenever Graph is
down; claims-only can only **narrow** a user's scope compared to a successful lookup, never widen it, so it is fail
closed with respect to access. The banner and the event make the narrowing visible. On the data plane a narrowed
answer would look like "no access" to the user, so a retryable 503 is honest.

### 2.7 Gateway work items for feature C (detailed in `../gateway-changes/design.md` §4)

1. Migration: `Departments.MemberGroups varchar(256)[] NOT NULL DEFAULT '{}'` (no index: never queried by group).
2. Admin API/UI *Organisation*: edit member groups (gateway admins; department admins read them); Graph search when
   `GroupLookup = Graph`, free text otherwise; audit `Update Department`.
3. `OidcOptions`: `GroupLookup`, `TenantId`, `RefuseGuests` (§1.4, §1.7). `DepartmentMembershipResolver` +
   `GraphGroupClient` in `AdminApi`, called from `OnTokenValidated` after `ApplyClaimMapping`
   (`AdminAuthentication.cs:123-127`).
4. `/bff/user`: `departmentSources`, `departmentLookup`. `admin.signin` (2002): attribute `DepartmentLookup`
   (attributes are only ever added, `docs/data-access.md:60-62`). New events 2007 `admin.group_lookup.failed`,
   2008 `admin.signin.refused` and Data API 3003 `data.token.refused` (tenant/guest refusals).
5. Data API: `memberGroups` on `/v1/catalog/departments`; `code` on `/v1/catalog/teams` (missing today).
6. Redis key `ume:groups:{oid}`; `UME_OIDC_GROUP_LOOKUP`, `UME_OIDC_TENANT_ID`, `UME_OIDC_REFUSE_GUESTS` in
   `compose.prod.yaml` and the Portainer templates.
7. Docs: `identity-and-roles.md` (Entra step 4 alternative "member groups"), runbook *Identity provider*.
8. Tests (§2.8), shared in spirit with ours.

The two implementations are deliberately the same code shape (`DepartmentMembershipResolver` takes the departments,
the principal, the options and an `IGroupMembershipSource`), so the class can be copied either way.

### 2.8 Tests (both systems)

| Test | Kind |
|---|---|
| Claim codes and group codes are merged without duplicates; sources reported correctly | unit |
| Group matching is case-insensitive; unmapped groups add nothing; inactive departments add nothing | unit |
| Membership never adds a role | unit |
| Overage token (`_claim_names.groups`, no `groups`) → Graph asked for all GUID candidates | unit, fake `IGroupMembershipSource` |
| Graph asked only for candidates not already in the claim and not in the cache's `checked` set; ≤ 20 ids per call | unit |
| Graph failure → sign-in succeeds with claim codes only, `departmentLookup = failed`, event 5007 / 2007, nothing cached | unit |
| Non-GUID candidates never sent to Graph | unit |
| Keycloak user `rag-group-member` (no attribute, group `Byggnadsnämnden`) sees department 3000 in `/bff/user` | integration (Keycloak container) |
| Mirror older than 24 h → group codes skipped | unit with `TestClock` |

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
    string? Email,                 // display / P2 Graph lookup only, never logged or stored
    ClientEntry Client,            // from the registry snapshot (§4.1)
    CredentialKind Credential,
    KeyEntry? Key,                 // key id, prefix, team, scopes (keys only)
    IReadOnlySet<string> Permissions,          // Chat.Use, Knowledge.Read, Mcp.Use, *.All (§7.2)
    MembershipFacts Membership,    // departments/groups and whether they are known (§4.3)
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
| MCP client | delegated token, `scp` `Mcp.Use` (§8) | `EntraUser` | `oid` | `azp` (kind `mcp-client`) | P2 |
| Worker → API internal attachment route (`POST /internal/attachments/{id}/chunks`, chat design §15.2) | internal service key, internal network only | `Application` | `svc:{keyId}` | client `rag-internal` (kind `internal`) | P2 |
| Chat role → knowledge role over HTTP (`Retrieval:Mode=Http`) | internal `ume-rag-sk-` service key | `Application` + forwarded decision | `svc:{keyId}` | client `rag-internal` (kind `internal`) | when split |
| Admin test panel | admin session (in-process, no HTTP) | `EntraUser` | admin `sub` | client `admin-console` (kind `internal`) | P1 |
| Browser following a citation link (`GET /v1/documents/{id}` and `/content` only) | signed download grant `?grant=g1.…` (knowledge design §4.6) | none (no subject; the grant carries ids only) | – | the grant's `cl` (client that received the citation) | P1 |

Rules:

- `SubjectId` is what conversations (`chat."Conversations"."SubjectId"`, data-model §2.3), per-user rate limits
  (`ume-rag:rl:user:{subjectHash}`) and `ToolCallEvents` use. It is pseudonymous but personal data, so security
  events carry it only where the gateway carries an actor (§10).
- Conversations are unique per (`ClientId`, `SubjectId`, `ExternalChatId`, `AssistantId`) (data-model §2.3, chat
  design §11.1), so an Open WebUI user id never meets an Entra `oid` in one namespace, and two Open WebUI instances
  never share conversations. Issued conversation ids of other clients are signed (`c1.…`, HMAC over client, subject
  and id); a foreign or forged id is a 404 with 5002 reason `conversation_not_owned` (§10).
- `EntraUser` uses `oid` when present, else `sub` (Keycloak and AD FS; both are stable per user).
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
| `StoresHistory`, `ClientRetentionDays`, `TaskModel`, `TaskModelResidency` | data-handling profile (R5) | – |
| `AllowedOrigins` | widget | – |
| `AllowedSubjectKinds` (derived from `Kind`, not stored) | §4.3 step 3 | – |
| `ResolveUsersByEmail bool` | P2: Open WebUI users mapped to Entra by email (§5.4) | new (§14) |

Database checks stay as designed: `StoresHistory ⇒ ceiling ≠ sensitive`, `mcp-client ⇒ ceiling ≠ sensitive`.
Pilot rows (seeded by the migration service in dev, created by a platform admin in production):

| Client | Kind | Ceiling | StoresHistory |
|---|---|---|---|
| Open WebUI pilot | `open-webui` | `internal` | yes (90 days, its own pruning) |
| `admin-console` | `internal` | `sensitive` | no |
| `rag-internal` | `internal` | `sensitive` | no |
| widget (P2) | `widget` | `public` | no |
| own chat client (P2) | `own-client` | `sensitive` | no |
| MCP clients (P2) | `mcp-client` | `public` (a platform admin may raise to `internal`) | n/a (treated as yes) |

### 4.2 Interface

`IAccessPolicy` lives in `Ume.Rag.Domain` and is **pure**: no I/O, no clock reads beyond an injected `now`, so the
policy matrix is a table-driven unit test.

```csharp
public interface IAccessPolicy
{
    AccessDecision Decide(RagPrincipal who, ResourceRef what, RagAction action, PolicySnapshot config);
    IReadOnlyList<ResourceRef> Filter(RagPrincipal who, IEnumerable<ResourceRef> candidates, RagAction action, PolicySnapshot config);
}

public enum RagAction { ListModels, Chat, Search, ReadDocument, McpCall, ToolCall, Feedback }

public sealed record AccessDecision(
    bool Allowed,
    DenyReason Reason,                 // None when allowed
    int HttpStatus,                    // 200/401/403/404/503
    Classification EffectiveLevel,     // the resource's level (drives residency)
    Residency? MaxResidency,           // null = no header (public)
    long ConfigVersion);
```

`PolicySnapshot` is the config snapshot (data-model §5) with `Version`, `Confirmed` (version confirmed ≤ 10 s ago),
clients, keys' scopes, assistants with audiences and collections, collections, skills, tools, data-protection
policies and the department mirror. Group membership that needs Graph is resolved **before** the call by
Infrastructure into `MembershipFacts { Departments, Groups, Known: bool }`, so the policy itself never waits.

### 4.3 Evaluation order (first failing step decides)

| # | Check | Deny reason | Status |
|---|---|---|---|
| 0 | Snapshot confirmed (`Version == lastConfirmed`, ≤ 10 s; data-model §5) | `config_unconfirmed` | 503 |
| 1 | Client exists and `IsEnabled`; the key's client is the client | `client_disabled` / `client_unknown` | 403 |
| 1a | Knowledge routes only: client kind in `Knowledge:OpenToClientKinds` (P1 `internal`; knowledge design §2.3); download grants are checked by their own re-check instead (knowledge design §4.6) | `client_not_allowed` | 403 |
| 2 | Credential kind allowed for the client kind (`open-webui`: sk key + JWT; `widget`: pk key; `integration`: sk key or app token; `own-client`/`teams`/`mcp-client`: delegated token; `internal`: service key or in-process) | `credential_not_allowed` | 403 |
| 3 | Subject kind allowed for the client kind (`widget` ⇒ `Anonymous` only; `Anonymous` only via `widget`) | `subject_not_allowed` | 403 |
| 4 | Permission for the action: delegated `scp` (`Chat.Use` for `ListModels`/`Chat`/`Feedback`, `Knowledge.Read` for `Search`/`ReadDocument`, `Mcp.Use` for `McpCall`), app `roles` (`Chat.Invoke.All`, `Knowledge.Read.All`), keys: key kind (`pk` ⇒ `ListModels`/`Chat`/`Feedback` only) | `permission_missing` | 403 |
| 5 | Resource exists and is live (assistant `State = published`, collection not deleted) | `not_found` | 404 |
| 6 | Key scope: if `Keys.Scopes` lists assistants/collections, the resource is in it | `key_scope` | 404 |
| 7 | **Classification ceiling**: resource level ≤ client ceiling | `ceiling_exceeded` (event 5005) | 404 |
| 8 | **Anonymous**: resource level = `public` | `anonymous_not_allowed` | 404 |
| 9 | **Data handling**: level policy `ClientsWithoutHistoryOnly` ⇒ `!client.StoresHistory`; `AllowedHistoryModes` contains the assistant's mode | `client_stores_history` | 404 |
| 10 | **Audience** (assistants; collections via the assistant, or via `Knowledge.Read` audiences in P2): any `AssistantAudiences` row matches: `client` = this client; `department` ∈ `Membership.Departments`; `group` ∈ `Membership.Groups`. Application subjects match only `client` rows | `audience_mismatch` | 404 |
| 11 | Step 10 needs a department/group row and `Membership.Known = false` | `membership_unavailable` | 503 |
| 12 | Tools (P2): tool policy (`tools/plan.md` §3, intersection server → skill → assistant → client) | `tool_not_allowed` | 403 |
| ✓ | Allowed: `EffectiveLevel` = resource level; `MaxResidency` per §4.7 | – | – |

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

1. No confirmed config version ⇒ every security-relevant decision (authentication with a key, any `Decide`) is 503
   `config_unconfirmed` + event 5004. `/health/ready` reports `config_unconfirmed` so the proxy drains the instance.
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
- Membership: Redis `ume-rag:groups:{oid}` 10 min (§2.4); Open WebUI email → `oid` in `ume-rag:owui-oid:{hmac(email)}`
  for 1 h (P2, §5.4).
- Nothing about a decision is cached in Redis.

### 4.7 Residency ceiling per request

`MaxResidency` comes from the **level of the data in the call** (platform plan §6), not from the client:

| Effective level | `x-ume-max-residency` | Interim (no feature B) |
|---|---|---|
| `public` | none | default team key |
| `internal` | `Eu` | `x-ume-residency` response check |
| `sensitive` | `OnPrem` | linked `onprem` key + response check |

Effective level for chat = the assistant's level (≥ all its collections, skills and tool servers, validated on
save); for condense and title calls inside a conversation the same; for P2 agent runs the highest of the assistant
and every tool result's server level so far. The Chat API sends the header from `AccessDecision.MaxResidency` and
`x-ume-on-behalf-of` = the assistant's team **id** (gateway design §5.2; codes can change), and the echo of that header
is required once gateway release A is in use. Header values are the gateway's `DataResidency` names (`Eu`, `OnPrem`,
case-insensitive). The gateway answer's `x-ume-residency` is checked, case-insensitively by order
`OnPrem < Eu < External`, against the same
value (chat-api §4).

### 4.8 Internal callers

- **Admin test panel**: runs in the admin process, builds a `RagPrincipal` for client `admin-console` with the
  admin's `sub`, and checks access with the admin's role scope (department of the assistant ∈ admin's departments),
  not with assistant audiences. Answers are not stored as conversations.
- **`rag-internal`**: the chat role sends `assistantId` and the already-made decision's level; the knowledge role
  recomputes the assistant's collections from its own snapshot (same version or newer) and refuses if the assistant's
  level is now above what was forwarded (the stricter answer wins).

## 5. Open WebUI trust

### 5.1 What Open WebUI sends

With `ENABLE_FORWARD_USER_INFO_HEADERS=true` and `FORWARD_USER_INFO_HEADER_JWT_SECRET` set, every request to a
connection carries `X-OpenWebUI-User-Jwt` (`backend/open_webui/env.py:1045-1054`), an HS256 JWT minted per request
(`backend/open_webui/utils/headers.py:42-53`):

```json
{ "sub": "<Open WebUI user id>", "email": "...", "name": "...", "role": "user|admin|pending",
  "iss": "open-webui", "iat": 1761730000, "exp": 1761730300 }
```

There is **no `aud`**, and if minting fails Open WebUI silently falls back to the unsigned `X-OpenWebUI-User-*`
headers (`headers.py:70-86`). The chat id travels unsigned in `X-OpenWebUI-Chat-Id` (`headers.py:196-199`). The
connection key goes in `Authorization: Bearer` (`headers.py:204-206`) unless `auth_type` says otherwise.

Open WebUI has **one connection** (ours, chat design §4.2–§4.3); its title/tags/follow-up tasks go to our virtual
model `owui-tasks` (answered by the client's on-prem `TaskModel`), so there is no gateway connection and the JWT goes
only to `rag-api`. The connection also sends the custom headers `X-Rag-Owui-Task`, `X-Rag-Owui-Message-Id` and
`X-Rag-Owui-User-Message-Id` (templated by Open WebUI, `headers.py:139-157`). They are **unsigned**: they only route
task requests and map messages and feedback **inside the caller's own conversation** (keyed by the JWT `sub`), never
grant access.

### 5.2 Validation (`OpenWebUiUserJwt`, new)

1. The request authenticated with an `ume-rag-sk-` key whose client has `Kind = open-webui` (§6). The JWT is never a
   credential on its own: replaying a captured JWT without the connection key gets nothing.
2. Header present and ≤ 4 KB; exactly one.
3. Header `alg` must be `HS256` (pinned `ValidAlgorithms = ["HS256"]`; `none`, `RS*`, `ES*` and a `kid` are refused).
   Key: `OpenWebUi:JwtSecret` (secret file `OpenWebUi__JwtSecret`, ≥ 32 bytes, generated by `init-deployment.sh`,
   repo-and-deployment §7.1); `OpenWebUi:ClientId` names the registry client it belongs to. A second instance later
   gets `OpenWebUi:Instances:<name>:{ClientId, JwtSecret}`.
4. `iss = "open-webui"`; `exp` and `iat` required; clock skew 30 s; `exp − iat ≤ OpenWebUi:MaxLifetimeSeconds`
   (600; Open WebUI's default is 300, `env.py:1050-1052`); `iat` not more than 30 s in the future.
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
  (forwarding is global); it has **one connection only** (ours): the earlier gateway connection for the on-prem task
  model is removed, tasks go to our `owui-tasks` model (chat design §4.2), so the JWT reaches only `rag-api`. Without
  `aud` a receiver could replay it within its lifetime, but it is useless without our connection key (§5.2 step 1);
  any connection added later must be a municipal service, never a third-party endpoint.
- The feedback import (chat design §14.2) calls Open WebUI's admin API with a service account's API key
  (`OpenWebUi__AdminApiKey`); Open WebUI restricts every API key to `/api/v1/evaluations/feedbacks/list`, and
  `ENABLE_ADMIN_CHAT_ACCESS=false` keeps its admins out of users' chats (chat design §4.3).
- Open WebUI settings required for user trust: `ENABLE_LOGIN_FORM=false` and `ENABLE_SIGNUP=false` (only Entra
  login; `backend/open_webui/config.py:1702-1704`), `ENABLE_OAUTH_SIGNUP=true` (`:2586`),
  `OAUTH_MERGE_ACCOUNTS_BY_EMAIL=false` (`:2593`), `OPENID_REDIRECT_URI=https://<owui host>/oauth/oidc/callback`
  (`:2652`, route `backend/open_webui/main.py:2957-2958`). Assignment required on its enterprise app decides who
  can sign in (plan §5, P1 audience).

### 5.4 User mapping

- P1: `SubjectKind = OwuiUser`, `SubjectId = sub`. Email and name from the JWT live only in the request's principal
  (e.g. for the P2 lookup below); they are never stored or logged.
- Without the JWT: `GET /v1/models` answers the **client-level** list (assistants with a `client` audience row for
  this client, level ≤ ceiling), because Open WebUI also lists models outside a user request (`routers/openai.py:94-105`
  sends user headers only when it has a user). Chat completions and feedback need the JWT (401 otherwise).
- `X-OpenWebUI-Auth-Type: api_key` (a user calling Open WebUI's API with a personal key, `headers.py:66-68`) is
  accepted; the subject is still the user.
- **P2, department/group audiences for Open WebUI users** (client flag `ResolveUsersByEmail`): look up the email via
  Graph `GET /users?$filter=mail eq '{email}' or userPrincipalName eq '{email}'&$select=id,userType,accountEnabled`
  (rag-api registration, `User.ReadBasic.All`, package 2), require exactly one enabled `Member`, cache `oid` 1 h, then
  §2.4 membership. No match ⇒ `Membership.Known = true` with no groups (the user simply gets client audiences).
  Trust caveat recorded in the client page: this makes Open WebUI's email the basis of a grant, so it is only enabled
  for instances whose login is Entra-only (5.3) and whose ceiling is `internal`.

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
arrives (data-model §5; Redis has no channels in our design, data-model §11). Because a missed `NOTIFY` is covered by
the 2 s poll and the 10 s confirmation rule (§4.5), a revoked key stops working within seconds, and a key is never
accepted while the version is unconfirmed. Creating a key bumps the version and clears the negative cache, as in the
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
   `AuthFailureThrottle.cs:7-106`).
2. Unknown or missing key ⇒ throttle count + failure bucket (`missing_key` / `invalid_key`), 401.
3. Status: `key_revoked`, `key_disabled`, `key_expired`, `owner_inactive` (team inactive in the mirror, or client
   disabled) ⇒ failure bucket with key id, 401/403.
4. `address_not_allowed` after the status checks, so a revoked key is reported as revoked wherever it is used
   (`:63-67`).
5. **New for `pk` keys**: `origin_not_allowed` (§6.5).
6. Config confirmed (§4.5), then the client layer (Open WebUI JWT, §5) and the policy.

Failures are aggregated exactly as the gateway's `AuthFailureRecorder` (bucket = reason, endpoint, key id, address;
flushed every 10 s, capped buckets with one overflow row, `AuthFailureRecorder.cs:23-48`,
`docs/data-access.md:85-105`) into a new `audit."AuthFailures"` table (§14) and one `rag.auth.failed` event per bucket.
Our bucket adds `Credential` and `ClientId`. The presented value is never stored or logged (`docs/data-access.md:103-105`).

### 6.4 Admin side

Keys page copies the gateway's endpoints (`KeyEndpoints.cs:79-133`): create (secret shown once), update, rotate
(immediate or 24 h grace), reveal/copy (platform admin, audited), revoke. Who may manage: `rag-platform-admin`
everywhere, `rag-department-admin` for keys of teams in their departments (`AdminContext.KeyTeams` pattern,
`AdminContext.cs:102-113`). Every change is `rag.admin.change`.

### 6.5 Widget `pk` keys (P2)

A `pk` key is public (it is in the page source), so it identifies the widget, not a secret holder:

- `Origin` must be present and match exactly (scheme, host, port; no wildcards) one entry of
  `Keys.AllowedOrigins ∩ Assistants.AllowedOrigins` of the requested assistant; else 403 `origin_not_allowed`. CORS
  response headers are sent only for that origin; `OPTIONS` preflight answered without authentication but only for
  allowed origins.
- Only `ListModels` (its scoped assistants), `Chat` and `Feedback`; only `public` assistants (§4.3 steps 4, 8).
- Anonymous session: on the first chat response the API issues `x-rag-session` (32 random bytes, base64url); the
  widget keeps it in `sessionStorage` and sends it back. `SubjectId = anon:` + hex HMAC-SHA256 of it with a key derived
  from the pepper (`HKDF(pepper, "widget-session")`), so the stored id cannot be turned back into the token.
- Rate limits per key (`ume-rag:rl:req:{keyId}`) and per client address (`ume-rag:rl:ip:{ipHash}`, data-model §11).
  `Origin` can be forged by non-browser clients, so the per-address limit and the `public`-only rule are the real
  protection.

## 7. OAuth tokens: machine clients and delegated users

### 7.1 Configuration (adapt `DataApiAuthentication`)

| Setting | Env | Default | Gateway counterpart |
|---|---|---|---|
| `RagApi:Authority` | `UME_RAG_API_AUTHORITY` | `UME_RAG_OIDC_AUTHORITY` | `DataApi:Authority` (`DataApiOptions.cs:15`, `compose.prod.yaml:181`) |
| `RagApi:Audience` | `UME_RAG_API_AUDIENCE` | `ume-rag-api` (Entra v2: the `rag-api` application id) | `DataApi:Audience` = `ume-data-api` (`DataApiOptions.cs:31-32`) |
| `RagApi:PermissionClaims` | – | `scope`, `scp`, `roles` | `DataApiOptions.cs:38` |
| `RagApi:TenantId` | `UME_RAG_API_TENANT_ID` | `UME_RAG_OIDC_TENANT_ID` | new |
| `RagApi:RefuseGuests` | – | `true` | new |
| `RagApi:AllowKeys` | – | `true` | `DataApi:AllowKeys` (`:18`) |

Authentication setup is the Data API's (`DataApiAuthentication.cs:21-64`): a policy scheme forwards to the key
handler when the request presents something shaped like our key (`PresentsKey`, `DataApiKeyAuthentication.cs:124-130`,
adapted to both prefixes and to `api-key`), otherwise to JWT bearer with issuer, audience and lifetime validation,
`MapInboundClaims = false` (`DataApiAuthentication.cs:33-47`). Tokens are accepted only if `RagApi:Authority` is set;
without it only keys work, as in the Data API (`:26-31`). A key's permissions and a token's never mix (`:66-68`).

Added checks after signature/issuer/audience/lifetime (JWKS cached by the handler's configuration manager):

1. `tid = RagApi:TenantId` (when set) ⇒ else `wrong_tenant`.
2. Guests refused (`acct = 1`, or `idp` ≠ issuer) ⇒ `guest_refused` (delegated tokens only).
3. Client = `ClientId(user)` (`client_id` ?? `azp` ?? `appid`, `DataApiAuthentication.cs:80-85`, without the `sub`
   fallback) looked up in `Clients.OAuthClientId`; unknown ⇒ `client_unknown`.
4. Token type: **app-only** when `idtyp = app` (Entra optional claim, requested on `rag-api`) or, on Keycloak, when the
   token has `client_id` and no `sid` (client-credentials tokens have no session; *to verify* on the Keycloak version
   in use). Otherwise delegated.

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
restricted to `allowedMemberTypes: ["Application"]` in Entra; distinct names avoid any clash between an app role and a
scope value on one registration. This is stricter than the Data API's "any configured claim" rule
(`DataApiAuthentication.cs:71-77`), because the Data API has no user tokens.

## 8. MCP protected-resource metadata (P2)

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
- Per-skill endpoints narrow further: only that skill's tools and collections, same principal and policy.
- **M1 unknowns** (postponed-decisions M1, decide at P2 start): whether MCP clients accept Entra's OIDC discovery
  document in place of RFC 8414 metadata; whether Entra v2 rejects the RFC 8707 `resource` parameter that MCP clients
  send (AADSTS error) or ignores it; Client ID Metadata Documents support in Entra and clients; which first-party client
  ids (VS Code, Copilot Studio, Claude) can be pre-authorised. Each is tested with the real clients before P2 ships.

## 9. On-behalf-of (P3, design only)

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
- `rag-tools` stays for service-identity calls (P2); it never performs OBO.

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
Tested in P2 (E1); until it passes, OBO tools are offered only to clients that send a real Entra token.

## 10. Security events

Copied pattern: one static partial `SecurityEvents` class in `Ume.Rag.Infrastructure`, category `Ume.Rag.Security`,
`[LoggerMessage(id, level, …, EventName = …)]` with level as constructor argument for CA1873
(`SecurityEvents.cs:63-114`), `Printable()` on every client-supplied value (`:30-49`), names and attributes only ever
added (`:7-11`, `docs/data-access.md:58-62`). Never logged: key material, tokens, JWTs, passwords, prompts, emails,
display names, group names. Allowed identifiers: key id + display prefix, client id, `SubjectId` (oid / Open WebUI id /
key id / anonymous HMAC), admin `sub` (as the gateway's `Actor`), truncated source address (`Api:Security:SourceAddress`).

Ranges: 5001–5099 data-plane identity and access; 5101–5199 content and ingestion; 6001–6099 admin. Repo-and-deployment
§9 ids are kept unchanged; new ids are additions.

| Id | Name | Emitted by | When | Attributes |
|---|---|---|---|---|
| 5001 | `rag.auth.failed` | api, admin (sign-in refusals) | Once per bucket per flush (§6.3); admin: per refused sign-in | `Reason`, `Credential` (`sk`, `pk`, `token`, `owui-jwt`, `oidc-signin`, `grant`), `Endpoint`, `KeyId`, `KeyPrefix`, `ClientId`, `TeamId`, `DepartmentId`, `SourceAddress`, `Count`, `FirstSeen`, `LastSeen` |
| 5002 | `rag.access.denied` | api, tools | `IAccessPolicy` denial on an explicit request (not `Filter`), at most once a minute per (subject, reason, resource) with `Count` (like `AccessDeniedThrottle`, `AdminActivity.cs:172`) | `Reason`, `Action`, `ResourceType`, `ResourceId`, `SubjectKind`, `SubjectId`, `ClientId`, `KeyId`, `ConfigVersion`, `SourceAddress`, `Count` |
| 5003 | `rag.residency.refused` | api, worker | gateway answered with a residency above the ceiling | (as step 2) |
| 5004 | `rag.config.stale_refused` | api, tools, worker | `config_unconfirmed` refusal, once a minute per instance with `Count` | `Service`, `CachedVersion`, `LastConfirmedAt`, `Count` |
| 5005 | `rag.ceiling.refused` | api | step 7 denial (instead of 5002) | 5002's attributes + `ResourceLevel`, `ClientCeiling` |
| 5006 | `rag.auth.throttled` | api | an address crossed `AuthFailuresPerMinute` (once per address per window) | `SourceAddress`, `Endpoint`, `Count` |
| 5007 | `rag.identity.lookup_failed` | admin, api | Graph membership or user lookup failed (§2.6) | `Service`, `Operation` (`checkMemberGroups`, `userByEmail`), `Status`, `SubjectId` |
| 5008 | `rag.key.unexpected_use` | api | a key in grace period used after its replacement was used (leak signal), once per key | `KeyId`, `KeyPrefix`, `RotatedToKeyId`, `SourceAddress` |
| 5009 | `rag.mcp.client_refused` | api | delegated token from an `azp` not registered as an enabled `mcp-client` | `ClientId` (azp), `SubjectId`, `SourceAddress` |
| 5010 | `rag.obo.refused` | tools (P3) | OBO exchange failed (`claims_required`, `consent_required`, `assertion_expired`, `error`) | `Reason`, `ToolServerId`, `SubjectId`, `ClientId` |
| 5011 | `rag.gateway.delegation_missing` | api (Warning) | after gateway release A: the response lacks the `x-ume-on-behalf-of` echo of the team sent; call aborted before the first byte (chat design §8.2) | `Step`, `AssistantId`, `TeamId`, `GatewayRequestId` |
| 5104 | `rag.parser.limit_exceeded` | worker | parser refused a file for a bomb or size limit (parser design §7.5) | `DocumentId`, `Limit` |
| 5105 | `rag.pipeline.refused` | worker | refused to run a published pipeline version that fails validation (engine design §17); SIEM alerts on any | `PipelineId`, `PipelineVersionId`, `Reason` |
| 5101–5103 | `rag.egress.blocked`, `rag.malware.detected`, `rag.pii.flagged` | worker | 5101/5102: connectors design §7, §11; 5103 on every transition to flagged (processing design §9.5) | 5103: `DocumentId`, `Classification`, `Types`, `Counts` (numbers only), `SourceId`, `RunId`, `Reflag`; SIEM rule for public references with a personnummer. Admin audit actions `pii.clear`, `gate.override` |
| 6001 | `rag.admin.change` | admin | every audit entry (gateway 2001) | `Actor`, `Action`, `EntityType`, `EntityId` |
| 6002 | `rag.admin.signin` | admin | sign-in, not extension (gateway 2002, `AdminAuthentication.cs:100-107`) | `Actor`, `SourceAddress`, `Roles`, `Departments`, `SessionId`, **`DepartmentLookup`** |
| 6003 | `rag.admin.signout` | admin | (gateway 2003) | `Actor`, `SourceAddress`, `SessionId` |
| 6004 | `rag.conversation.access` | admin | conversation review events | unchanged |
| 6005 | `rag.admin.session.extended` | admin | *Stay signed in* (gateway 2004) | `Actor`, `SessionId`, `ExpiresAt`, `SourceAddress` |
| 6006 | `rag.admin.access.denied` | admin | 403 for a signed-in user, once a minute per user (gateway 2005) | `Actor`, `Method`, `Path`, `Count`, `SourceAddress`, `Roles` |
| 6007 | `rag.admin.data.read` | admin | reads of the audit log, keys, conversation lists (gateway 2006) | `Actor`, `Path`, `Query`, `SourceAddress` |

`Reason` values of 5001: the gateway's set (`missing_key`, `invalid_key`, `key_revoked`, `key_expired`,
`key_disabled`, `owner_inactive`, `address_not_allowed`; `SecurityEvents.cs:51-61`) plus `origin_not_allowed`,
`user_jwt_missing`, `user_jwt_invalid`, `user_jwt_expired`, `user_jwt_role`, `user_jwt_mismatch`, `token_invalid`,
`token_expired`, `wrong_tenant`, `guest_refused`, `client_unknown`, `permission_missing`, `grant_invalid`,
`grant_expired` (download grants, knowledge design §4.6). 5002 `Reason` = §4.3 reasons plus `conversation_not_owned`
(an issued conversation id whose MAC does not match the caller's client and subject, chat design §11.1). The MAC key
is `Security:ConversationIdKeys` (secret files `Security__ConversationIdKeys__<kid>`, ≥ 32 bytes, made by
`init-deployment.sh`; newest signs, all verify, as the download-grant keys).

Gateway counterparts (gateway design §10): our 5001 with `wrong_tenant`/`guest_refused` = gateway 2008 (admin) or
3003 (Data API); our 5007 = gateway 2007.

SIEM alerts to add to the gateway's list (`docs/security-events.md:29-34`): any 5008; 5005 from one client repeatedly
(a client probing above its ceiling); any 5003 or 5011 from the chat role; 5004 for more than a minute (config monitor broken); 5007 bursts (Graph outage
or permission removed); 5001 `user_jwt_invalid` at all (Open WebUI secret mismatch or forgery).

## 11. Keycloak realm (dev and CI)

`dev/keycloak/ume-rag-realm.json`, realm `ume` (same issuer path shape as the gateway's, `ume-realm.json:2`), adapted
from the gateway's file; passwords and secrets are import placeholders like `${UME_DEV_USER_PASSWORD}`
(`ume-realm.json:27`, `:119`). Extends repo-and-deployment §4.2:

| Element | Content |
|---|---|
| User profile | declares `departmentCodes`, `teamCodes` (Keycloak ≥ 24 drops undeclared attributes, `docs/identity-and-roles.md:148-149`) |
| Realm roles | `rag-platform-admin`, `rag-department-admin`, `rag-pipeline-editor`, `rag-editor`, `rag-viewer`; app permissions `Knowledge.Read.All`, `Chat.Invoke.All` |
| Client scopes | `Chat.Use`, `Knowledge.Read`, `Mcp.Use` (optional client scopes, each with an audience mapper `ume-rag-api`, `include.in.token.scope` on, so they appear in `scope`) |
| Client `ume-rag-admin` | confidential, standard flow only, PKCE S256, redirect and post-logout URIs for `https://localhost:5174` and the adminapi https port (as `ume-realm.json:28-37`); mappers copied: `roles` realm-role mapper (`:41-52`), `departmentCodes` and `teamCodes` attribute mappers (`:54-79`), all to the ID token; **`groups`** group-membership mapper (full path off, ID token on) |
| Client `ume-rag-api-dev` | client credentials only (as `ume-data-dev`, `:84-107`); audience mapper `ume-rag-api`; realm-role mapper to `roles` (access token); service account roles `Knowledge.Read.All`, `Chat.Invoke.All` |
| Client `ume-rag-chat-dev` | **new**: public client, code + PKCE, redirect `http://localhost:*` style loopback for test tools; default scope `Chat.Use`, optional `Knowledge.Read`, `Mcp.Use`; for delegated-token and MCP tests |
| Client `open-webui-dev` | confidential, code flow, redirect `http://localhost:<owui port>/oauth/oidc/callback`; `email` in ID token; used when `OpenWebUi:Enabled` |
| Groups | `Byggnadsnämnden` (member: `rag-group-member`), `rag-pilot-users` (members: all test users) |
| Users | `rag-platform-admin` (1000, 3000, 5000), `rag-department-admin` (1000), `rag-pipeline-editor` (1000), `rag-viewer` (1000), `rag-group-member` (no attribute; via `Byggnadsnämnden` → department 3000), **`rag-no-role`** (1000, no role: every `/api` call 403), **`rag-chat-user`** (no admin role; for delegated chat tokens) |
| Seed | the FakeGateway catalogue (repo-and-deployment §4.3) returns department 3000 with `memberGroups: ["Byggnadsnämnden"]`, so the org sync fills the mirror in dev |

Development uses `Oidc:GroupLookup = Claims`; the Graph path is covered by unit tests with a fake
`IGroupMembershipSource` (no Graph mock server).

## 12. Approval package 1 as Bicep (Microsoft Graph extension)

### 12.1 Content

| Object | Change | Needs admin consent / privileged role |
|---|---|---|
| Gateway admin registration (shared) | add RAG redirect URIs (`https://<rag admin host>/signin-oidc`, `/signout-callback-oidc`); add `rag-*` app roles (Users/Groups); our own client secret is added by an operator, not Bicep (§12.2); optional ID-token claims `email`, `acct`; `requiredResourceAccess` Graph `GroupMember.Read.All` (Application) (+ `User.ReadBasic.All` if §2.4's check shows it is needed) | **yes**: the Graph application permission needs admin consent (an app role assignment on the Microsoft Graph service principal) |
| Its service principal | `appRoleAssignmentRequired = true` (already, if the gateway followed `docs/identity-and-roles.md:169-170`) | no |
| `open-webui` registration (new) | Web redirect `https://<owui host>/oauth/oidc/callback`; ID-token optional claims `email`, `acct`; delegated Graph `openid profile email` (user consent is enough, but granted tenant-wide by admin to avoid prompts); its own client secret (§12.2) | admin consent for the delegated scopes (recommended) |
| Its service principal | `appRoleAssignmentRequired = true` | no |
| Pilot groups (new) | `Ume.Rag pilot users` (Open WebUI access), `Ume.Rag platform admins`, `Ume.Rag department admins – <pilot department>` (names agreed with IT; no convention exists, plan answered question 5) | `Group.Create` for the deployer |
| App role assignments | pilot users group → `open-webui` (default access); admin groups → `rag-platform-admin` / `rag-department-admin` on the shared registration | assigning on an app the deployer does not own needs `AppRoleAssignment.ReadWrite.All` or app ownership |

### 12.2 Credentials

The Graph Bicep extension cannot create client secrets (`passwordCredentials` are not writable from Bicep; *to
verify*). **Decision (2026-10-29): client secrets, identical to the gateway's setup and code.** Our BFF gets its own
secret on the shared registration, created by an operator with `az ad app credential reset --append` (so the gateway's
secret is untouched) and passed to `init-deployment.sh --oidc-client-secret-file` (`Oidc__ClientSecret`), exactly as
in the gateway; the Graph client (§2.4) uses the same secret. Open WebUI's OIDC client supports only a client secret
(`OAUTH_CLIENT_SECRET`), created the same way. Certificates (`private_key_jwt`) can later be added to **both** systems
together (a `gateway-changes` candidate), not to ours alone.

Because `keyCredentials` and `appRoles` are arrays that a Bicep deployment **replaces as a whole**, the shared
registration's module must declare the gateway's roles, redirect URIs and `keyCredentials` (if any) too. So `infra/entra/`
becomes the owner of the shared registration's definition; the gateway's client secret (a `passwordCredential`) is not
touched by Bicep. Any manual portal change to the registration is overwritten on the next run, which the PR plan
shows first.

### 12.3 Sketch (`infra/entra/`)

```bicep
// bicepconfig.json: "extensions": { "microsoftGraphV1": "br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:<version to verify>" }
extension microsoftGraphV1

param gatewayAdminHost string
param ragAdminHost string
param openWebUiHost string
param graphAppId string = '00000003-0000-0000-c000-000000000000'
param groupMemberReadAllId string           // Graph app role id of GroupMember.Read.All, looked up by the workflow

var gatewayRoles = [ /* gateway-admin, department-admin, viewer, team-admin: ids copied from the live registration */ ]
var ragRoles = [
  { id: guid('rag-platform-admin'),   value: 'rag-platform-admin',   displayName: 'RAG platform admin',   allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: everything' }
  { id: guid('rag-department-admin'), value: 'rag-department-admin', displayName: 'RAG department admin', allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: own departments' }
  { id: guid('rag-pipeline-editor'),  value: 'rag-pipeline-editor',  displayName: 'RAG pipeline editor',  allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: pipelines' }
  { id: guid('rag-viewer'),           value: 'rag-viewer',           displayName: 'RAG viewer',           allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: read' }
  { id: guid('rag-editor'),           value: 'rag-editor',           displayName: 'RAG editor',           allowedMemberTypes: ['User'], isEnabled: true, description: 'Ume.Rag: forms in own departments (P2)' }
]

resource adminApp 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'ume-admin'                   // set once on the existing registration (write-once alternate key)
  displayName: 'Umeå kommun – LLM gateway och Ume.Rag admin'
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      'https://${gatewayAdminHost}/signin-oidc', 'https://${gatewayAdminHost}/signout-callback-oidc'
      'https://${ragAdminHost}/signin-oidc',     'https://${ragAdminHost}/signout-callback-oidc'
    ]
  }
  appRoles: concat(gatewayRoles, ragRoles)
  optionalClaims: { idToken: [ { name: 'email' }, { name: 'acct' } ] }
  requiredResourceAccess: [ { resourceAppId: graphAppId, resourceAccess: [ { id: groupMemberReadAllId, type: 'Role' } ] } ]
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
// twin-admin.bicep (fallback): application 'ume-rag-admin' with ragRoles only, same claims and permission.
```

Not in Bicep: the admin consent for `GroupMember.Read.All` (the app role assignment on the Graph service principal)
and the group → app role assignments of §12.1 (admin groups → `rag-*` roles, pilot users → `open-webui` default
access). They are the approval itself: an Entra administrator makes them in the portal (or runs the `az rest`
commands the workflow prints) after reviewing the run. Group membership of the pilot groups is maintained by IT, not
by IaC.

### 12.4 Workflow (`entra.yml`, repo-and-deployment §8.1)

1. **PR** touching `infra/entra/**`: `bicep build` + `bicep lint`; `azure/login` (OIDC federation, environment
   `entra-plan`, read-only identity); `az deployment group what-if` if it supports Graph resources (*to verify*), else a
   script that prints the desired vs live `az ad app show` diff for the touched applications. Plan as a PR comment.
2. **Dispatch** (`environment: entra-test` / `entra-prod`, required reviewers = the municipality's approvers or a
   change-ticket link): look up Graph app role ids (`az ad sp show --id 00000003-…`), `az deployment group create
   --template-file infra/entra/main.bicep --parameters @infra/entra/<env>.bicepparam`. The deployment needs an Azure
   subscription and resource group for the deployment record (Graph Bicep deploys through ARM).
3. **Deployer identity**: a dedicated app with a federated credential for this repository and environment only;
   Graph permissions `Application.ReadWrite.OwnedBy` (it is made an **owner** of the shared, Open WebUI and twin
   registrations) and `Group.Create`; no `AppRoleAssignment.ReadWrite.All` and no directory role, so it cannot grant
   consent to itself or others.
4. **After apply**: the job summary lists what still needs a person: admin consent and group → role assignments
   (§12.3), Open WebUI client secret, group membership. The ticket id is recorded in P0 checklist item 17.

## 13. Test cases

### 13.1 Copied from the gateway (adapted names)

| Gateway test | Ours | Notes |
|---|---|---|
| `OidcClaimMappingTests` (`tests/Ume.LlmGateway.AdminApi.Tests/OidcClaimMappingTests.cs:13`, `:22`, `:31`, `:43`, `:56`) | `Ume.Rag.AdminApi.Tests/OidcClaimMappingTests` | role values `rag-*` |
| `AdminSessionTests` (`AdminSessionTests.cs:38`, `:57`, `:73`, `:97`) | same | ticket store, 12 h limit |
| `AuthFailureThrottleTests` (`tests/Ume.LlmGateway.Gateway.Tests/AuthFailureThrottleTests.cs:20`, `:39`, `:54`) | `Ume.Rag.Api.Tests` | unchanged |
| Gateway `SecurityEventTests` (`SecurityEventTests.cs:18`, `:40`, `:66`, `:83`, `:109`) | `Ume.Rag.Api.Tests/AuthFailureTests` | buckets, no presented value, overflow, address truncation |
| `DataApiKeyTests` (`tests/Ume.LlmGateway.DataApi.Tests/DataApiKeyTests.cs:13`, `:38`, `:59`) | `Ume.Rag.Api.Tests/KeyAndTokenTests` | a token can never claim key permissions and the reverse |

### 13.2 Unit (`Ume.Rag.Domain.Tests`, `Ume.Rag.AdminApi.Tests`)

- **Policy matrix** (table-driven, one row per combination): subject kind × client kind × credential × resource level ×
  ceiling × audience kind × membership known/unknown × key scope → expected reason and status. Must include: Open WebUI
  (`internal`) never sees a `sensitive` assistant; MCP client at `public` never sees `internal`; anonymous never sees
  `internal`; `StoresHistory` client refused where the level requires no-history clients; app subject never matches a
  department audience; empty audience list denies everybody; unconfirmed snapshot denies everything with 503.
- Residency derivation: `public` → none, `internal` → `Eu`, `sensitive` → `OnPrem`; agent run level rises with a tool
  result.
- `Filter` returns exactly the resources `Decide` allows (property test over random snapshots).
- Feature C resolver (§2.8). Tenant and guest checks (`acct=1`, foreign `idp`, wrong `tid`).
- Role resolution: several roles add up; non-`rag-*` roles ignored; no role ⇒ every policy fails.
- Key hasher: both prefixes, wrong prefix for the stored kind, gateway key shape refused, display prefix length.

### 13.3 Integration (`Ume.Rag.Api.Tests`, `Ume.Rag.AdminApi.Tests`, `Ume.Rag.IntegrationTests` with Keycloak and Postgres containers)

- Sign in as each Keycloak user; `/bff/user` shows the expected roles, departments and sources (P0 checklist #9).
- Token from `ume-rag-api-dev` (client credentials) calls search with `Knowledge.Read.All`; the same token on chat
  without `Chat.Invoke.All` gets 403 `permission_missing`.
- Delegated token from `ume-rag-chat-dev` with `Chat.Use` lists only assistants allowed for client `ume-rag-chat-dev`.
- Key revoked in the admin API stops working on the api within the poll interval (≤ 3 s in the test), with event 5001
  `key_revoked`; key created clears the negative cache.
- Config monitor stopped (Postgres `NOTIFY` and poll blocked) ⇒ after 10 s every keyed request is 503
  `config_unconfirmed` + 5004; resumes when the monitor confirms.
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

All applied in the step 3 review commit (2026-10-29).

1. **`../platform/data-model.md`**
   - §2.1 `Departments`: `MemberGroupIds uuid[]` → `MemberGroups varchar(256)[]` (group claim values, §2.2); GIN
     index unchanged.
   - §2.1 `AssistantAudiences`: for `Kind = group` store the value in a new `GroupValue varchar(256)` (null for other
     kinds); `SubjectId` becomes nullable for `group` rows; PK → unique index over (`AssistantId`, `Kind`,
     `coalesce(SubjectId::text, GroupValue)`).
   - §2.1 `Clients`: `EntraAppId uuid` → `OAuthClientId varchar(200)` (Keycloak client ids are strings), unique; `Kind`
     gains `internal`; new `ResolveUsersByEmail bool` (P2).
   - §2.4 new `audit."AuthFailures"` (copy of the gateway's table + `Credential`, `ClientId`), `INSERT` for api and
     admin, retention with the audit step; add to the §1.2 grants table.
   - §11 Redis: add `ume-rag:owui-oid:{hmac(email)}` (1 h, P2); value shape of `ume-rag:groups:{oid}` is
     `{checked, member}`.
   - §12.9: Data API field is `memberGroups` (strings), credential is a Data API key (`catalog.read`), not a
     `rag-sync` client.
2. **`../platform/repo-and-deployment.md`**
   - §4.2 realm: realm roles `Chat.Use` → client scopes `Chat.Use`/`Knowledge.Read`/`Mcp.Use`, app roles
     `Knowledge.Read.All` + `Chat.Invoke.All`; add user profile, client `ume-rag-chat-dev`, users `rag-no-role` and
     `rag-chat-user`, group `rag-pilot-users`; FakeGateway catalogue returns `memberGroups`.
   - §6.3 secrets: add `OrgSync__DataApiKey` (admin); §7.1 `init-deployment.sh`: `--org-sync-key-file`.
   - §6.1 networks: in P2 `rag-api` needs egress to `login.microsoftonline.com` and `graph.microsoft.com` (JWKS,
     Graph membership, OBO in P3); P1 only the admin needs it.
   - §8.1 `entra.yml`: deployer identity and steps as §12.4; note that the shared registration's definition moves to
     `infra/entra/` (§12.2).
   - §9 security events: add 5006–5010 and 6005–6007; attribute `DepartmentLookup` on 6002.
3. **`../gateway-changes/plan.md`** §3b feature C: member groups are group claim values (§2.2); Data API field
   `memberGroups`; work items of §2.7; new event 2007 `admin.group_lookup.failed`; `/bff/user` additions. New small
   item: `tid` and guest refusal for admin sign-in and the Data API (`TenantId`, `RefuseGuests`).
4. **`plan.md`** (this folder) §3: "Groups are stored by object id" → "by group claim value (Entra object ids;
   names for Keycloak/AD FS)"; §2 package 1: own credential is a client secret created by an operator; consent and role assignments are
   granted by a person after the workflow; §4 table: app permissions `Chat.Invoke.All`/`Knowledge.Read.All`.
5. **`../chat-api/plan.md`** §8.1: conversations of Open WebUI users are keyed by (client, Open WebUI user id, chat id),
   not by `oid`; §5 "Stored per user (Entra `oid`)" → "per `SubjectId`" (§3).
6. **`../admin/plan.md`** §3 roles table: role-less users get 403 everywhere; organisation page links to the gateway
   for member groups; keys page per §6.4.
7. **`../postponed-decisions.md`** M1: add the RFC 8707 `resource`-parameter and OIDC-discovery questions (§8).

## 15. Answered questions (2026-10-29)

1. **Shared registration ownership**: `ume-rag/infra/entra/` owns the shared admin registration's definition (the
   gateway's roles and redirect URIs included, since Bicep replaces those arrays whole); the gateway's docs keep the
   manual steps for other adopters (§12.2).
2. **Admin credential**: a client secret of our own, identical to the gateway's setup; certificates later for both
   systems together (§12.2).

## 16. To verify (before or during P0/P1)

- Graph `checkMemberGroups` for `/users/{id}` with application permissions: whether `User.ReadBasic.All` is needed
  besides `GroupMember.Read.All`.
- Graph Bicep extension: current version, `passwordCredentials` not writable, `members` relationship on groups,
  what-if support, `uniqueName` adoption of an existing registration.
- Keycloak: `client_id` claim in client-credentials tokens and `sid` absence (§7.1 step 4) on the version Aspire
  pins.
- Entra: `acct` optional claim values and `idp` behaviour for B2B guests in ID and access tokens.
- Open WebUI tag chosen at P0 (V1): JWT fields and fallback behaviour unchanged from `headers.py:42-86`.
