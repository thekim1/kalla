# Prompt: gateway session for the Ume.Rag pilot (release 1, shared Keycloak, production)

Paste everything below the line into a new Claude session started in `~/git/llmgateway`. Written by the Ume.Rag main
session, 2026-10-30.

---

You work in the **LLM gateway** repo (`~/git/llmgateway`, github.com/thekim1/llmgateway). The user owns it. A second
project, **Ume.Rag** (`~/research/rag`, github.com/thekim1/ume-rag-platform), a RAG and chat platform for Umeå
kommun, will call this gateway for every model call. Its pilot needs three things from the gateway. The gateway runs
only in dev today.

**Start** with `git pull` on `main` (the latest release on `origin` is v0.8.6). **Read first:** this repo's `HANDOVER.md` and
`README.md`, `CHANGELOG.md` (release practice), `docs/introduction.md`, `docs/runbook.md`,
`docs/identity-and-roles.md`, `docs/production-setup.md`, and `docs/upgrade-guide.md`. From the RAG repo (read only;
never edit it), read:
- `plans/gateway-changes/design.md` (the specification of every change below, with file and line citations into this
  repo);
- `plans/gateway-changes/adr-drafts.md` (ADR drafts 018–020; release 1 needs only the residency-ceiling one);
- `01-general-idea.md` §10, Decisions table rows "P1 pilot sign-in" and "M2 and test hosting".

Keep this repo's own conventions: release practice, rolling-update safety, the 7-day package rule, tests,
security-event numbering and docs. Where the RAG design and this repo disagree, this repo's facts win: report the
difference instead of forcing it.

## 1. Release 1 (needed before the RAG pilot goes live)

Scope, exactly (`gateway-changes/design.md` §1 table):
- **Feature B: per-request residency ceiling.**
  - the `x-ume-max-residency` request header (§3.1);
  - semantics and placement in `GatewayRequestHandler` (§3.2);
  - the 403 vs 503 rule and the `x-ume-residency` response header on every answer (§3.3);
  - interactions, including `/v1/models` filtered by the ceiling (§3.4, and the §2 pipeline table);
  - the key flag **`RequireMaxResidency`** and migration `KeyResidencyCeiling` (§3.5);
  - the tests of §3.6.
- **FakeLlm embedding `dimensions`** (§6) and the **tool passthrough tests** (§7.2).
- **Not in release 1:** feature C (member groups, tenant/guest refusal), feature A (delegation, run grouping headers),
  the rerank endpoint. Those are release 2, in P2.
- Docs: the `docs/admin-api.md` rows for the new headers and codes (§ "Changes to other documents"), and an
  upgrade-guide entry.

Done when: release 1 is tagged by this repo's release practice. A RAG-side contract check passes: RAG's
`Ume.Rag.FakeGateway` (in the RAG repo) enforces the same header semantics, so compare the two and list any
difference.

## 2. Shared Keycloak (decided 2026-10-30)

One Keycloak serves both the gateway admin and Ume.Rag (admin UI and Open WebUI) for the pilot. Entra ID comes in P2.
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
  municipal AD comes later, with no app change.
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

## 3. Gateway production (prepare now, deploy late)

The user wants testing on the cheapest possible host and production-style deployment postponed as long as possible
(Decisions table "M2 and test hosting"). So prepare the production deployment, but deploy only when the RAG pilot is
about to go live (RAG phase P1b).
- **Providers:** there is no on-prem model hardware yet (register M2 triggered). Until there is, the pilot's
  embeddings and its condense and answer models are **EU-hosted** providers routed by the gateway. List candidate EU
  providers and models with their residency classification. The data protection impact assessment (G1, later) must
  list the processor agreement.
- A production checklist and the host requirements, kept cheap.

## Reporting back

The RAG main session tracks this as an external dependency of its P1 plan (`plans/p1/`). When release 1 is tagged,
and when the Keycloak project is ready, tell the user, or open an issue labelled `main` in
github.com/thekim1/ume-rag-platform, with the tag or commit and anything that differs from
`gateway-changes/design.md`.
