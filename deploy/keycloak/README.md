# RAG's realm additions for the shared Keycloak

RAG's part of the pilot's Keycloak realm `ume`, which is shared with the LLM gateway (identity design §11.2, Decisions
table "P1 pilot sign-in"). The gateway repo owns the Keycloak project and the base realm. These files add RAG's roles,
clients and settings to that realm. Hand-off J8 takes them to the gateway session; J11 is the realm with them imported.

| File | What it holds | Applied as |
|---|---|---|
| `rag-additions.json` | The `rag-*` realm roles; the clients `ume-rag-admin`, `open-webui` and `ume-rag-api`, all with *full scope allowed* off; the role scope mappings of `ume-rag-admin` (so its `roles` claim carries the `rag-*` roles and no gateway role) | One partial import with `ifResourceExists=FAIL`, then the scope mappings |
| `rag-realm-settings.json` | **Realm-wide, separable:** the user-profile attributes `departmentCodes` and `teamCodes` (shared with the gateway), password policy, brute-force protection, the OTP policy and the browser flow `rag-browser` (OTP required for `rag-platform-admin` and `rag-department-admin`) | Added to the realm; a value that changes is printed |
| `rag-additions.local.json` | **Test or dev realms only:** the local AppHost redirect URIs, web origins and post-logout URIs of `ume-rag-admin` and `open-webui` (issue #95 part 3) | Merged into the clients with `--include-local-redirects` |
| `apply-additions.py` | Applies either part or both through the admin REST API (Python standard library only) | `--part additions\|settings\|all`, `--include-local-redirects` |

The realm-wide part is a file of its own, so the gateway session can choose whether those settings live here or in
the gateway's base realm (identity design §16, open questions 2–4). If they move to the base realm, apply only
`--part additions`.

## Applying

```bash
export KEYCLOAK_ADMIN_PASSWORD=...                      # an admin of the master realm (or --admin-realm)
export UME_RAG_ADMIN_PUBLIC_HOST=rag-admin.intern.umea.se
export UME_RAG_OPENWEBUI_HOST=chat.intern.umea.se
export UME_RAG_OIDC_CLIENT_SECRET=...                   # = deploy/.env's ume-rag-admin secret file
export UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=...         # = --openwebui-oidc-client-secret-file
python3 -I deploy/keycloak/apply-additions.py --url https://keycloak.example --realm ume --admin-user admin
```

The files use `${NAME}` placeholders, as Keycloak's start-up import does. The script fills them from the environment
and stops before any call if one has no value.

- **Additions are all or nothing.** If a role or client of the same name exists, the partial import fails and
  changes nothing. A second run therefore fails with "already exists". To change a client later, edit it in the admin
  console, or delete it and run again.
- **Settings:**
  - A user-profile attribute that is already declared is kept as it is.
  - If the flow `rag-browser` already exists, the script stops.
  - Every realm value it changes is printed with its old value (for example `failureFactor: 30 -> 10`).
  - A failure part-way can leave a half-built `rag-browser` flow. Delete the flow and run `--part settings` again.

**Redirect URIs.** `rag-additions.json` holds only the pilot hosts, from the environment. The local AppHost's URIs are
in `rag-additions.local.json`:
- `ume-rag-admin`: `https://localhost:5174` (admin UI) and `https://localhost:7402` (admin API).
- `open-webui`: `http://localhost:8080`.

`--include-local-redirects` adds them, so a developer can point an AppHost at a **test or dev** realm. Never use it on
the production realm: there, a redirect to whatever runs on a user's localhost must not be accepted.

**PKCE on `open-webui`** is not required yet. Requiring it in Keycloak (`pkce.code.challenge.method` `S256`) only works
once Open WebUI sends a challenge (`OAUTH_CODE_CHALLENGE_METHOD=S256`), so the two are set together and checked in
item 18's run. `ume-rag-admin` already requires S256.

## OTP for admins

`rag-browser` is Keycloak's browser flow plus three conditional sub-flows after the password:

1. `rag-browser OTP` asks for the OTP of every account that has set one up (as Keycloak's own *Conditional 2FA*).
2. `rag-browser OTP platform admin` applies to a `rag-platform-admin` for whom (1) did not run. The OTP form is
   required, so Keycloak shows the OTP set-up page.
3. `rag-browser OTP department admin` does the same for `rag-department-admin`.

The result:
- An admin sets up OTP at the first sign-in.
- After that, (1) asks for the OTP once, even for an account that holds both admin roles.
- Other accounts sign in with the password alone, unless they set up OTP themselves.

To require OTP for the gateway's admins too, add a sub-flow like (2) with their role.

## Tested

`tests/Ume.Rag.AdminApi.Tests/RealmAdditionsTests.cs` runs Keycloak 26.6.4 with a minimal stand-in for the gateway's
base realm (`tests/Ume.Rag.AdminApi.Tests/Keycloak/ume-realm.json`: a gateway client and roles, `departmentCodes`
already declared). It applies both parts with this script, then checks:

- A second import is refused.
- Each client's token carries only its own system's roles.
- The password policy refuses a short password.
- An editor signs in through the admin BFF without OTP.
- An admin sets up OTP at the first sign-in and enters it once at the next.
