# Gateway ADR drafts 018–020

Row 020 is not a draft: the gateway records its integrations decisions as its own ADRs 020 and 021
(`docs/integrations.md`, decided by user, 2026-11-11, #580), which replace team delegation. Row 020 below states what
Källa relies on from them; the gateway's text is the source of truth.

Drafts of three rows for `docs/adr.md` in the gateway repo (`~/git/llmgateway`, table at `docs/adr.md:3-23`; append the rows right after 017). 018 is
feature B, 019 feature C, 020 feature A of [`../archive/area-plans/gateway-changes.md`](../archive/area-plans/gateway-changes.md). Since the [weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)
they go in with two releases ([`design.md`](design.md) §13): **018 is accepted with release 1** (before the pilot),
**019 with release 2** (P2); 020 comes with the gateway's integration phases. Same table and cell style as 013–017: one dense paragraph per
cell.

| ADR | Decision | Rationale / consequences |
|---|---|---|
| 018 | A request can lower its own residency ceiling with `x-fors-max-residency`, and never raise it; a key can require the header | A key's allowed residencies are set by whoever manages the key (team and department admins included) and can be widened later, so a caller that must keep some data on-prem or in the EU cannot rely on the key alone. Any key may send `x-fors-max-residency: OnPrem \| Eu \| External` (the `DataResidency` names, case-insensitive; no aliases, so `EU` works but `Any` does not: omit the header instead). Anything else, or more than one value, is `400 invalid_request`; ignoring a malformed ceiling would fail open. A key with `RequireMaxResidency` is refused (`400 invalid_request`) when it omits the header, so a caller's forgotten header fails instead of running without a ceiling. The ceiling is one more hard constraint in candidate selection, intersected with the key's residencies and the PII on-prem restriction, after routing rules and fallback lists are computed (as ADR 013 requires), so no rule, fallback or retry can go above it. When no configured target of the planned models is within the ceiling the request fails with `403 max_residency_not_met`; when such targets exist but are drained or circuit-open it is `503 no_eligible_provider` with `Retry-After`, so the caller waits instead of concluding the model is forbidden; there is never a silent fallback to a wider residency. It applies to every endpoint that reaches a provider, live sessions included, and `GET /v1/models` then lists only the models with a target within the ceiling (today it filters by the key's residencies only), so a caller can check what it may use at a level. `x-fors-residency` keeps reporting where the request went, so a caller that must be sure checks it before using the answer: during a rolling update an older instance ignores the header. Parsing is a span comparison and the intersection a lookup in eight precomputed sets, so the hot path gains no allocation and no round trip. |
| 019 | Department scope can also come from group membership in the token, and admin sign-in refuses other tenants and guests | Municipalities often have no department attribute in their directory, but do have groups. A department gets optional **member groups** (group claim values: Entra object ids, AD FS or Keycloak names, matched case-insensitively like `Oidc:RoleGroups`), edited by gateway admins. At sign-in the scope is the `departmentCodes` claim plus every active department whose member groups appear in the token's group claim, written back as ordinary `departmentCodes` claims, so every scoped query is unchanged; membership never grants a role. Only the claim is read: a group the token does not show (not emitted, or overage) adds nothing, so scope can only narrow. Member groups are exposed on the Data API's `catalog.read` departments list so another system (Källa) can apply the same rule with the same data. Independently, `Oidc:TenantId` (token `tid` must match) and `Oidc:RefuseGuests` (default on: `acct = 1`, or an `idp` that differs from both the issuer and `https://sts.windows.net/{tid}/`) refuse sign-in and Data API tokens of other tenants and guests (`admin.signin.refused` 2008, `data.token.refused` 3003). Keycloak and AD FS emit neither claim, so the guest check is a no-op there; Entra adopters who deliberately admit guests turn it off. |
| 020 | A platform is an integration; its credential keys can act as keys assigned to it, and an assigned key cannot be used directly (the gateway's own ADR, `docs/integrations.md`) | What Källa relies on: each Källa service authenticates with one credential key of the integration *Källa*; a department's admins make an ordinary key in their own team, mark it as used through Källa, and set its models, residencies, PII policy, content guard, limits and budget there; Källa stores only that key's id and sends `x-fors-on-behalf-of: <key id>`, and the request then runs as that key (its allow-lists, policies, limits, budgets, team, department and usage record) with the id echoed. An assigned key has no usable secret, so nothing per assistant is copied, leaked or rotated. The credential key's own allow-lists do not apply to acted requests; Källa asked in #580 that its `RequireMaxResidency` and `AllowedResidencies` keep bounding them (pending, [`design.md`](design.md) §5.3). Supersedes the draft "a key can act for listed teams" (`DelegatedTeamIds`, `ApplyTeamPolicy`). |

Line to replace the current acceptance line (`docs/adr.md:25-26`) with, filling in each date when its rows are
accepted (018 with release 1, 019 with release 2; weakness review §5; 020 and 021 are the gateway's own). With release
1 only the 018 row is appended and the line ends "018 on YYYY-MM-DD"; release 2 appends 019 and the second date:

> Accepted for POC on 2026-10-07 (013-014 on 2026-10-09, 015 on 2026-10-10, 016 on 2026-10-12, 017 on 2026-10-16,
> 018 on YYYY-MM-DD, 019 on YYYY-MM-DD). Reassess durable accounting, HA, egress isolation and per-use-case legal
> controls before production approval.

## Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../archive/passes/simplification-pass.md) §6–§7: one row per release (B → 018, C → 019, A → 020)
became one combined release; 019 no longer contains the Graph `checkMemberGroups` lookup (P2, with
`GroupMember.Read.All`, its cache and event 2007); 020 no longer applies the effective team's request policy; 018 now
answers 503 instead of 403 for drained in-ceiling targets and adds `RequireMaxResidency`.

## Deferred or removed (weakness review)

Per [weakness review §5](../archive/passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):
the three rows are no longer accepted together. 018 (feature B) is accepted with release 1 before the pilot; 019
(feature C) and 020 (feature A) with release 2 in P2. The row texts are unchanged: 018 already stands without
delegation (its `/v1/models` sentence applies to every key).
