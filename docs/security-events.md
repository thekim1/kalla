# Security events

The services log security events for the municipality's SIEM under one logger category, **`Kalla.Security`**,
with stable numeric ids and event names (identity design §10). This page lists them, says what each one means, what is
never in them, and which alerts are worth setting up. The source of truth for the code is
`src/Kalla.Infrastructure/Security/SecurityEvents.cs`. `SecurityEventsDocTests` fails when an event there is
missing from the table below.

## Getting them to the SIEM

Set `OTEL_EXPORTER_OTLP_ENDPOINT` on `kalla-api`, `kalla-worker` and `kalla-admin`. In an OpenTelemetry Collector, route
log records whose category is `Kalla.Security` to the SIEM:
[`deploy/otel/collector-siem.example.yaml`](../deploy/otel/collector-siem.example.yaml) does (syslog RFC 5424, with
Splunk, Elastic and Sentinel alternatives; copied from the gateway's). With Compose, set `KALLA_OTEL_ENDPOINT` in
`deploy/.env`; on Kubernetes `OTEL_EXPORTER_OTLP_ENDPOINT` in the overlay. The event id is the record's `EventId`, the name its `EventName`, and each attribute below is
a structured field.

## Rules

- **Ids and names are never changed or reused.** New events and new attributes are only added. Ranges:
  5001–5099 data-plane identity and access, 5101–5199 content and ingestion, 6001–6099 admin.
- **Never logged:** key material, tokens, JWTs, passwords, prompts, answers, document text, e-mail addresses, display
  names, group names.
- **Identifiers that may appear:** key id and its display prefix, client id, `SubjectId` (Entra `oid`, Open WebUI user
  id, key id or an anonymous HMAC), the admin's `oid` (`Actor`; `sub` where the identity provider has no `oid`, as
  Keycloak), session id (random, given at sign-in; never the cookie), and the source address.
- **Client-supplied text** (paths, query strings, user agents) is logged with control characters replaced by `?`, so it
  cannot start a forged log line. Logs are JSON outside development.
- **Source address:** the client's address. When the connection comes from a proxy listed in the admin's trusted
  proxies (Settings, *Network*), both kalla-api and the admin take the right-most untrusted `X-Forwarded-For` entry;
  otherwise it is the connection's address, so behind a reverse proxy that is not listed it is the proxy's.

## Events

Status: **P0** emitted today; **P1** defined in `SecurityEvents.cs` and emitted by the P1 code that raises it (the worker's egress guard and scanner); otherwise the phase it comes with.

| Id | Name | Level | Emitted by | When | Attributes | Status |
|---|---|---|---|---|---|---|
| 5001 | `rag.auth.failed` | Warning | api; admin | A refused credential (keys and tokens: once per bucket per flush). Admin: every refused sign-in, `Credential` = `oidc-signin` (`Reason` `callback_failed`: a callback that could not be completed, such as a forged, expired or replayed one, or one started before the sign-in values changed) | `Reason`, `Credential`, `Endpoint`, `SourceAddress` (P1 adds `KeyId`, `KeyPrefix`, `ClientId`, `TeamId`, `DepartmentId`, `Count`, `FirstSeen`, `LastSeen`) | P0 (admin sign-in) |
| 5002 | `rag.access.denied` | Warning | api | An access-policy denial on an explicit request, at most once a minute per subject, reason and resource | `Reason`, `Action`, `ResourceType`, `ResourceId`, `SubjectKind`, `SubjectId`, `ClientId`, `KeyId`, `ConfigVersion`, `SourceAddress`, `Count` | P1 |
| 5003 | `rag.residency.refused` | Warning | api, worker | The gateway answered from a residency above the ceiling the call sent; the answer was discarded | `Ceiling`, `Observed`, `Endpoint`, `GatewayRequestId` | P0 |
| 5004 | `rag.config.stale_refused` | Warning | api, worker | Security-relevant decisions refused because the configuration was not confirmed within `Config:MaxUnconfirmedSeconds`; once a minute per instance | `Service`, `CachedVersion`, `LastConfirmedAt`, `Count` | P0 |
| 5005 | `rag.ceiling.refused` | Warning | api | A client asked for something above its classification ceiling | 5002's attributes + `ResourceLevel`, `ClientCeiling` | P1 |
| 5006 | `rag.auth.throttled` | Warning | api | An address crossed the refused-credential limit per minute | `SourceAddress`, `Endpoint`, `Count` | P1 |
| 5007 | – | – | – | Reserved (P2 Graph member-group lookup) | – | reserved |
| 5008 | `rag.key.unexpected_use` | Warning | api | A key in its grace period used after its replacement was used: a leak signal | `KeyId`, `KeyPrefix`, `RotatedToKeyId`, `SourceAddress` | P1 |
| 5009 | `rag.mcp.client_refused` | Warning | api | A delegated token from an `azp` that is not an enabled MCP client | `ClientId`, `SubjectId`, `SourceAddress` | P2 |
| 5010 | `rag.obo.refused` | Warning | tools | An on-behalf-of exchange failed | `Reason`, `ToolServerId`, `SubjectId`, `ClientId` | P3 |
| 5011 | `rag.gateway.delegation_missing` | Warning | api, admin, worker | A `2xx` or `304` answer to a call made as an assigned gateway key lacks the `x-fors-on-behalf-of` echo of the key id sent, or echoes another, while `Gateway:RequireDelegationEcho` is on; the call was aborted before use | `Step`, `AssistantId` or `CollectionId`, `GatewayKeyId`, `GatewayRequestId` | P1 |
| 5012 | `rag.tool.definition_changed` | Warning | api, admin | A tool's definition changed, was removed or added since it was pinned | `ToolServerId`, `ToolId`, `ToolName`, `Kind`, `PinnedHash`, `ObservedHash` | P2 |
| 5013 | `rag.tool.upstream_refused` | Warning | api, admin | A tool server refused our credential; once a minute per server and reason | `ToolServerId`, `Reason`, `Status`, `Count` | P2 |
| 5014 | `rag.content.tag_neutralised` | Information | api, admin | Reserved tags neutralised in untrusted text | `SourceKind`, `ResourceId`, `Count` | P2 |
| 5015 | `rag.tool.result_refused` | Warning | api, admin | A tool result refused (`too_large`, `level_refused`, `protocol_error`) | `ToolServerId`, `ToolId`, `Reason`, `ResultBytes`, `ResultLevel`, `EffectiveLevel` | P2 |
| 5101 | `rag.egress.blocked` | Warning | worker | A fetch to a blocked or private address was refused; at most once a minute per (source, reason) | `Reason` (`private_address`, `embedded_address`, `scheme`, `port`, `userinfo`, `blocked_host`, `redirect_downgrade`), `AddressClass` (`loopback`, `rfc1918`, `link_local`, `cgnat`, `ula`, `multicast`, `reserved`), `Purpose`, `SourceId`, `RunId`, `Classification`, `HostHash` (HMAC of the host under `Security:CrawlKeyPepper`), `Host` (`public` sources only), `Count` | P1 |
| 5102 | `rag.malware.detected` | Warning | worker | ClamAV found malware in a fetched or uploaded file; the file is quarantined | `Signature`, `Sha256`, `Origin` (`upload`/`crawl`), `SourceId`, `RunId`, `DocumentId` (if any), `Classification` | P1 |
| 5103 | `rag.pii.flagged` | Warning | worker | A document became flagged for personal data | `DocumentId`, `ChunkSetId`, `Classification`, `Types`, `Counts` (numbers only), `SourceId`, `RunId`, `Reflag` | P1 |
| 5104 | `rag.parser.limit_exceeded` | Warning | worker | The parser refused a file for a size or bomb limit | `DocumentId`, `Limit` | P1 |
| 5105 | `rag.pipeline.refused` | Warning | worker | A published pipeline version failed validation and was not run | `PipelineId`, `PipelineVersionId`, `Reason` | P1 |
| 5106 | `rag.malware.scan_off` | Warning | worker | The worker started with `ClamAv:Mode = off`: files are stored without a malware scan. Once at every start | – | P1 |
| 6001 | `rag.admin.change` | Information | admin | Every audit entry: a configuration change (`Action` `create`, `update`, `sync`, …), with the details in the audit log | `Actor`, `Action`, `EntityType`, `EntityId` | P0 |
| 6002 | `rag.admin.signin` | Information | admin | A sign-in (not an extension) | `Actor`, `SourceAddress`, `Roles`, `Departments`, `SessionId` | P0 |
| 6003 | `rag.admin.signout` | Information | admin | A sign-out | `Actor`, `SourceAddress`, `SessionId` | P0 |
| 6004 | `rag.conversation.access` | Information | admin | A comment reveal or conversation view, logged before decrypting | `Actor`, `RequestId`, `Event`, `Scope`, `ConversationId`, `Classification`, `AssistantId` | when stored content is allowed (A1) |
| 6005 | `rag.admin.session.extended` | Information | admin | *Stanna inloggad* | `Actor`, `SessionId`, `ExpiresAt`, `SourceAddress` | P0 |
| 6006 | `rag.admin.access.denied` | Warning | admin | A 403 for a signed-in user; at most once a minute per user, with the count since the last | `Actor`, `Method`, `Path`, `Count`, `SourceAddress`, `Roles` | P0 |
| 6007 | `rag.admin.data.read` | Information | admin | A read of a sensitive list: the audit log (P0); keys, feedback, the access log and conversation views as they arrive | `Actor`, `Path`, `Query`, `SourceAddress` | P0 |
| 6008 | `rag.admin.setup.opened` | Warning | admin | Setup mode opened at kalla-admin's start (identity design §19): `Kind` `first_run` (never completed) or `reopen` (a new `Setup:Reopen` value). Never the setup code | `Kind`, `EndsAt` | P1 |
| 6009 | `rag.admin.setup.code_refused` | Warning | admin | Wrong setup codes, sampled: at most one event per 10 seconds per instance, with the number since the last event (the code checks are limited to 20 a minute per instance; a wrong code never voids the code) | `SourceAddress` (of the latest), `Count` | P1 |
| 6010 | `rag.admin.setup.session` | Warning | admin | A setup session started for a correct setup code | `SessionId` (`setup-` and a random id, never the cookie), `SourceAddress`, `ExpiresAt` | P1 |
| 6011 | `rag.admin.setup.test` | Warning | admin | A setup test finished: kalla-admin's test sign-in, or Open WebUI's back-channel test (no person signs in); or callbacks with an unknown, replayed or expired test (`Reason` `unknown_test`, sampled like 6009) | `Target` (`kalla-admin`, `open-webui`), `Result` (`passed`, `failed`), `Reason`, `Subject` (the tested person's `oid`, else `sub`; none for `open-webui`), `PlatformAdmin`, `SessionId`, `SourceAddress`, `Count` (1, or the sampled `unknown_test` callbacks since the last event) | P1 |
| 6012 | `rag.admin.setup.completed` | Warning | admin | Setup completed: the tested values are saved and setup is closed; the audit entry names the same person | `Actor` (the tested person), `SessionId`, `SourceAddress` | P1 |
| 6013 | `rag.admin.setup.openwebui_saved` | Warning | admin | Open WebUI's sign-in values saved in setup mode after their test (setup stays open); the audit entry names the same person | `Actor` (the person of the session's passed kalla-admin test), `SessionId`, `SourceAddress`, `ClientVerified` (whether the test confirmed the client's credentials) | P1 |

### `Reason` values

- **5001:** the gateway's `missing_key`, `invalid_key`, `key_revoked`, `key_expired`, `key_disabled`,
  `owner_inactive`, `address_not_allowed`, plus `origin_not_allowed`, `user_jwt_missing`, `user_jwt_invalid`,
  `user_jwt_expired`, `user_jwt_role`, `user_jwt_mismatch`, `token_invalid`, `token_expired`, `wrong_tenant`,
  `guest_refused`, `client_unknown`, `permission_missing`, `grant_invalid`, `grant_expired`. In P0 the admin emits
  `wrong_tenant` and `guest_refused` (identity design §1.7). `client_not_allowed`: a valid key of another client than the
  deployment's own Open WebUI client asked kalla-api for Open WebUI's sign-in settings (`/internal/openwebui/sign-in`).
- **6011:** `passed`, or the step that failed: `browser_mismatch`, `secret_required`,
  `deployment_secret_without_authority`, `provider_error`, `token_refused`, `token_endpoint_unreachable`, `tls`,
  `insecure_endpoint`, `redirected`, `id_token_missing`, `id_token_invalid`, `nonce_mismatch`, `keys_unavailable`,
  `wrong_tenant`, `guest_refused`, `session_expired`, `unknown_test`. `open-webui`: `passed`, `client_refused` (the
  token endpoint answered `invalid_client` or 401), or, passed without confirming the client, `client_unverified`,
  `token_endpoint_unreachable`, `tls`, `redirected`, `insecure_endpoint`.
- **5002:** the access-policy reasons (identity design §4.3), `conversation_not_owned`, and from P2 the tool reasons
  (`tool_not_allowed`, `tool_grant_missing`, `tool_clearance`, `tool_unapproved`, `tool_drifted`, `tool_write_disabled`,
  `client_tool_policy`, `skill_audience`).

Gateway counterparts: our 5001 with `wrong_tenant`/`guest_refused` is the gateway's 2008 (admin) or 3003 (Data API);
6001–6007 follow the gateway's 2001–2006.

## Alerts worth setting up

- any **5008** (a rotated-out key still in use after its replacement);
- **5005** from one client repeatedly (probing above its ceiling);
- any **5003** or **5011** from the chat role;
- any **5004** (configuration unconfirmed beyond the grace window);
- any **5102** (malware);
- any **5106** on a deployment that is meant to scan (the malware scan was turned off);
- **5103** on a `public` reference with a personnummer;
- any **5001** `user_jwt_invalid` (Open WebUI secret mismatch or forgery);
- **5001** `wrong_tenant` or `guest_refused` repeatedly;
- several **6006** from one user (probing for rights);
- any **6008** that no deployer asked for, and bursts of **6009** (someone guessing the setup code);
- any **6012** (the sign-in provider or client changed), and any **6013** (Open WebUI's provider or client changed);
- **6002** from an address outside the municipal networks, or one `Actor` from two addresses at once;
- **6005** many times for one `SessionId` (a session kept alive unattended);
- **6007** of `/api/audit` outside office hours;
- an audit entry whose `SessionId` never had a **6002**;
- from P2: any **5012**, bursts of **5013**, any **5015** `level_refused`, **5014** from one tool or document
  repeatedly.
