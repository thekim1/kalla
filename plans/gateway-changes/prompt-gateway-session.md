# Prompt: gateway session for the Ume.Rag pilot (release 1, shared Keycloak, production)

Paste everything below the line into a new Claude session started in a clone of the gateway repo. Written by the
Ume.Rag main session, 2026-10-30; updated 2026-11-05 (gateway v0.8.9, identity providers).

**Status (2026-11-06): done.** Release 1 shipped as gateway **v0.9.0** (2026-11-06): residency ceiling (ADR 018),
embeddings checked per input, the FakeLlm's real-sized embeddings, the optional `POST /v1/rerank`, the optional Keycloak
project (26.8.1, keycloak-config-cli with `additions/`) and the EUPL-1.2 licence. What remains on the gateway side is
operations, not code: deploy v0.9.0 and the Keycloak project for the pilot, copy Ume.Rag's
`deploy/keycloak/rag-additions.json` into `additions/`, pilot keys, and the Berget AI rerank provider. Keep this prompt
as the record of what was asked; a new gateway session starts from the gateway's `HANDOVER.md`.

---

You work in the **LLM gateway** repo (github.com/thekim1/fors). The user owns it. A second project, **Ume.Rag**
(github.com/thekim1/ume-rag-platform), a RAG and chat platform for Umeå kommun, calls this gateway for every model
call. Its pilot needs the things below from the gateway. The gateway runs only in dev today.

**Start** with `git pull` on `main` (the latest release on `origin` was v0.8.9, 2026-11-03, when this was written). If this machine has no clone
of the RAG repo, clone it into a scratch directory; you only read it. **Read first:** this repo's `HANDOVER.md` and
`README.md`, `CHANGELOG.md` (release practice), `docs/introduction.md`, `docs/runbook.md`,
`docs/identity-and-roles.md`, `docs/production-setup.md`, and `docs/upgrade-guide.md`. From the RAG repo (read only;
never edit it), read:
- `plans/gateway-changes/design.md` (the specification of every change below, with file and line citations into this
  repo);
- `plans/gateway-changes/adr-drafts.md` (ADR drafts 018–020; release 1 needs only the residency-ceiling one);
- `plans/decisions.md`, entries "P1 pilot sign-in" and "M2 and test hosting".

**Citations are stale.** `gateway-changes/design.md` cites this repo at `e25b491` (v0.8.5). Since then v0.8.9 split
`GatewayRequestHandler` along its seams (`RequestStages`, `RequestAccounting`, `ContentGuardStage`,
`RealtimeRequestHandler`, `ModelListEndpoint`) and merged the rate limit and budget reservation into one Redis script
when no routing rule applies (`IAdmissionStore`). Find each cited step by what it does, not by its line, and say in your
report where the ceiling check landed. The ceiling is still applied after routing rules and fallback lists are computed
(ADR 013), including on the `IAdmissionStore` path, where the estimate is made before routing.

Keep this repo's own conventions: release practice, rolling-update safety, the 7-day package rule, tests,
security-event numbering and docs. Where the RAG design and this repo disagree, this repo's facts win: report the
difference instead of forcing it.

## 1. Release 1 (needed before the RAG pilot goes live)

Scope, exactly (`gateway-changes/design.md` §1 table):
- **Feature B: per-request residency ceiling.**
  - the `x-fors-max-residency` request header (§3.1);
  - semantics and placement in `GatewayRequestHandler` (§3.2);
  - the 403 vs 503 rule and the `x-fors-residency` response header on every answer (§3.3);
  - interactions, including `/v1/models` filtered by the ceiling (§3.4, and the §2 pipeline table);
  - the key flag **`RequireMaxResidency`** and migration `KeyResidencyCeiling` (§3.5);
  - the tests of §3.6.
- **FakeLlm embedding `dimensions`** (§6) and the **tool passthrough tests** (§7.2).
- **Embedding batches vs the context window** (found 2026-10-31, RAG session 11, to-verify #43). The gateway compares
  the whole request's estimated input tokens (body size / 4) with the deployment's `ContextWindow` for embeddings too
  (in v0.8.5 `GatewayRequestHandler.cs:194-196` and `GatewayRequestHandler.Stages.cs:188-204`, now in `RequestStages`;
  400 `context_length_exceeded`). For
  embeddings the window applies per input. The RAG worker sends up to 64 inputs and 16k tokens per request, so with
  BGE-M3 at `ContextWindow` 8192 most full batches would be refused. Fix in release 1: check embeddings per input (the
  longest input's estimate), or skip the check for the embeddings endpoint. Until then RAG caps its batches below the
  window (worker setting), and an embedding deployment can be left without a `ContextWindow`.
- **Rerank endpoint, pulled forward (user, 2026-11-05):** design §7.4 now, as its own small release that can land after
  release 1 without blocking it: path and request shape (query plus passages in, one score per passage), the
  residency ceiling and `x-fors-residency` like every call, and an **EU-hosted reranker until on-prem hardware exists**
  (M2), `bge-reranker-v2-m3` on-prem later. Say which EU provider and model, and its price.
- **Not in release 1:** feature C (member groups, tenant/guest refusal), feature A (delegation, run grouping headers).
  Those are release 2, in P2.
- Docs: the `docs/admin-api.md` rows for the new headers and codes (§ "Changes to other documents"), and an
  upgrade-guide entry.

Done when: release 1 is tagged by this repo's release practice. A RAG-side contract check passes: RAG's
`Kalla.FakeGateway` (in the RAG repo) enforces the same header semantics, so compare the two and list any
difference.

## 2. Shared Keycloak (decided 2026-10-30)

One Keycloak serves both the gateway admin and Ume.Rag (admin UI and Open WebUI) for the pilot. Entra ID comes in P2.

**Identity providers (user, 2026-11-05).** Ume.Rag already depends on the gateway, so for now it uses the gateway's
Keycloak. Two requirements shape how you build it:
- **AD and Entra ID must work in production**, because some organisations will use them. Keep both routes open and
  document them in `docs/identity-and-roles.md`:
  - **Brokered:** Keycloak in front of the organisation's directory. Entra ID is an OIDC identity provider in the
    realm; on-prem AD comes in through LDAP user federation (read-only bind account) or AD FS as an identity provider.
    Mappers give the same token shape as a local account: roles (or groups that `Oidc:RoleGroups` maps),
    `departmentCodes`, `teamCodes` and `groups`, so neither app changes.
  - **Direct:** both apps point at Entra ID or AD FS without Keycloak, through the `Oidc:*` settings the gateway already
    has. Ume.Rag does the same (Decisions "P1 pilot sign-in": switching is configuration, not code).
  - **Test the brokered route** in the realm's test set-up with a second Keycloak realm or container as a stand-in
    upstream OIDC provider: a brokered user signs in to both systems with the expected roles and `departmentCodes`.
    Also test LDAP federation, against a test LDAP container if that is cheap. List the Entra-specific steps (app
    registration, group claim, `tid`) as documented but unverified until a test tenant exists.
- **Ume.Rag must be able to run standalone later**, with its own Keycloak or directly against the organisation's
  Entra ID or AD FS, without the gateway's sign-in. The shared realm must not tie the two systems together:
  - Ume.Rag's part stays a self-contained additions file (its clients and `rag-*` roles).
  - Ume.Rag's sign-in needs nothing from the gateway's base realm except the shared user attributes
    (`departmentCodes`, `teamCodes`), which a standalone realm declares itself.
  - Decide where the realm-wide settings live (Q3 below) so that both a shared and a standalone set-up can use them.
  - Report anything on the gateway side that would make a standalone Ume.Rag harder.
  - RAG's position on identity §16 Q2–Q4 and Q8 is in the Decisions row "M-2 open design calls"; settle
    them with it.
- **Its own Compose project** in this repo (for example `deploy/keycloak/compose.yaml`), run on the gateway's
  production host and operated with the gateway. Its database is `keycloak` in the **gateway's Postgres** (a
  separate database and role), so the gateway's backups cover it.
- **Realm `ume`**, holding this repo's clients and roles (from `dev/keycloak/ume-realm.json`, without test users)
  plus a **realm-additions file that Ume.Rag's stream B delivers** (clients `ume-rag-admin`, `open-webui`,
  `ume-rag-api`; the `rag-*` realm roles). Turn "full scope allowed" off per client, so a token carries only its own
  system's roles. `departmentCodes` stays a shared user attribute. Design how the two parts are imported and kept in
  step (an import job, `kcadm`, or a documented procedure), and say which.
- **Production mode** per Keycloak's own docs: hostname, behind the municipal reverse proxy, `/health/ready` for the
  proxy, `http-max-queued-requests`, a pinned image by digest (the RAG repo uses
  `quay.io/keycloak/keycloak:26.6.4@sha256:0aae0de7…cbce4`; match or newer under the 7-day rule).
- **Sizing:** about 1.25 GB RAM per instance (Keycloak's sizing guide); one instance for the pilot.
- **Accounts:** local Keycloak accounts for the pilot, with OTP required for admin roles. LDAP federation to the
  municipal AD, or Entra ID brokering, comes later as realm configuration, with no app change (see *Identity
  providers* above).
- Docs: a runbook section for account administration (create, disable, reset, OTP) and backups.
- **Answer these before J11** (Ume.Rag `plans/identity/design.md` §16 "Open for P1", questions 1–4 and 6, numbered as
  there, full text there; added 2026-10-31):
  - Q1: Who administers realm `ume` in production (accounts, role grants, `departmentCodes`, OTP resets, the realm's
    own admin users and their OTP), and who holds which Keycloak admin role.
  - Q2: How Ume.Rag's realm additions are imported and kept in step with the base realm, how a later change to
    Ume.Rag's `deploy/keycloak/**` reaches production and who applies it, and how a mistake in one system's part is
    kept from breaking the other's sign-in.
  - Q3: Whether the realm-level settings (password policy, brute-force protection, the user profile with
    `departmentCodes`/`teamCodes`, the OTP flow) live in this repo's base realm or in Ume.Rag's additions file.
  - Q4: Which accounts count as admins for OTP (every `rag-*` role, or only `rag-platform-admin`/`rag-department-admin`,
    plus `gateway-admin`) and how OTP is enforced (per-account required action or a conditional flow on role).
  - Q6: Account lifecycle: who creates, disables and removes pilot accounts, how initial credentials and OTP enrolment
    reach people, and who keeps `departmentCodes` and group membership current.

Done when: the project starts locally in production mode, both systems' dev apps sign in against it with the merged
realm, and a database restore of the gateway brings Keycloak back.

**Host (user, 2026-10-31):** the gateway's test/dev instance already runs on a fixed host with HTTPS (public
certificate), EU embedding and chat models, and Keycloak. That Keycloak holds the shared realm `ume` for the RAG pilot
(J11), and the instance is Ume.Rag's interim J10. Ume.Rag already runs against it with interim `Eu` keys and
`text-embedding-3-large` at 1024 dimensions (Decisions "Pilot embedding model, U-b"). Still missing: the instance on
gateway v0.10.0 with the integration *Ume.Rag* and its three credential keys, `rag-admin`'s with the scope
`organisation.read` for the organisation and model syncs (Ume.Rag admin design §4.10; no Data API key is needed any
more). Ask the user how to hand the keys over; never write them into either repository.

## 3. Gateway production (prepare now, deploy late)

The user wants testing on the cheapest possible host and production-style deployment postponed as long as possible
(Decisions table "M2 and test hosting"). So prepare the production deployment, but deploy only when the RAG pilot is
about to go live (RAG phase P1b).
- **Providers:** there is no on-prem model hardware yet (register M2 triggered). Until there is, the pilot's
  embeddings and its condense and answer models are **EU-hosted** providers routed by the gateway. List candidate EU
  providers and models with their residency classification. The data protection impact assessment (G1, later) must
  list the processor agreement.
- A production checklist and the host requirements, kept cheap.

## 4. Licence (decided 2026-10-31)

Ume.Rag is licensed under **EUPL-1.2** (its `LICENSE`, PR #65) and copies gateway files by design. The gateway has no
`LICENSE` yet: add the same EUPL-1.2 text (unchanged, SPDX `license-list-data/text/EUPL-1.2.txt`) and a README
**Licence** section, as Ume.Rag's #65 did. Both repositories stay private until the municipality approves publication.

## Reporting back

The RAG main session tracks this as an external dependency in `plans/roadmap.md` ("Waiting on people"). When release 1 is tagged,
and when the Keycloak project is ready, tell the user, or open an issue labelled `main` in
github.com/thekim1/ume-rag-platform, with the tag or commit and anything that differs from
`gateway-changes/design.md`.
