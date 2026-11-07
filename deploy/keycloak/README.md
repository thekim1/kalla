# RAG's part of the shared Keycloak realm

The pilot's Keycloak realm `ume` is shared with the LLM gateway (identity design §11.2, Decisions "Gateway release 1
and shared Keycloak as built"). The gateway repo owns the Keycloak project (`deploy/keycloak` there, Keycloak 26.8.1)
and the base realm file `realm/10-gateway.json`. Ume.Rag delivers one additions file. The gateway's `realm-import`
job (keycloak-config-cli) applies the base file and then every file in the operator's additions directory, in name
order, on every `docker compose up`. Every resource type is imported with `no-delete`: a file adds and updates, and
never removes what another file or an administrator made. The gateway's `docs/keycloak.md` (*Realm files*) describes
the job.

| File | What it holds | Where it goes |
|---|---|---|
| `rag-additions.json` | `"realm": "ume"`; the 7 `rag-*` realm roles; the clients `ume-rag-admin` (confidential, code + PKCE S256), `open-webui` (confidential, code, callback `/oauth/oidc/login/callback`: Open WebUI v0.12.5, which still serves the legacy `/oauth/oidc/callback` too) and `ume-rag-api` (audience only, no flow), all with *full scope allowed* off; the role scope mappings of `ume-rag-admin`, so its `roles` claim carries the `rag-*` roles and no gateway role | The gateway's additions directory (production and test) |
| `rag-additions.local.json` | **Test or dev realms only:** the two sign-in clients again, with the pilot URIs plus the local AppHost redirect URIs, web origins and post-logout URIs (issue #95 part 3) | A test realm's additions directory, next to `rag-additions.json`. **Never in production** |
| `standalone/10-rag-realm.json` | **A standalone Ume.Rag realm only:** the realm itself, with the realm-wide settings the gateway's base file holds in the shared realm (password policy, brute-force protection, no self-registration, the user profile with `departmentCodes` and `teamCodes`) | In place of the gateway's base file, see *Standalone* |

The additions declare no realm settings and no `userProfile`. keycloak-config-cli replaces the whole user profile with
a file's `userProfile`, so declaring one here would wipe the gateway's. `departmentCodes` and `teamCodes` come from the
base file. **No OTP** is enforced by these files (#390 Q4): sign-in is password policy plus brute-force protection,
and MFA returns with brokered Entra ID/AD.

## Shared realm (the pilot)

For the gateway operator, on the gateway host:

1. Copy `rag-additions.json` from a tagged Ume.Rag release into the additions directory
   (`deploy/keycloak/additions/`, or the directory `UME_KEYCLOAK_ADDITIONS_DIR` names). Copy no other file from here.
2. Put the values of its placeholders in the env file that `UME_KEYCLOAK_IMPORT_ENV` names. Keep it with the
   gateway's secrets, readable only by the operator:

   ```bash
   UME_RAG_ADMIN_PUBLIC_HOST=rag-admin.intern.umea.se      # the admin UI's host
   UME_RAG_OPENWEBUI_HOST=chat.intern.umea.se              # Open WebUI's host
   UME_RAG_OIDC_CLIENT_SECRET=<the admin API's Oidc:ClientSecret>
   UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=<Open WebUI's OIDC client secret>
   ```

   The secrets must equal the ones Ume.Rag's deployment uses (`deploy/.env` and its secret files). Never commit them.
3. Run `docker compose -f deploy/keycloak/compose.yaml --env-file deploy/keycloak/.env up -d --wait`. Only the import
   job runs again. A placeholder without a value fails the import at this file; the files before it stay applied.

Placeholders are keycloak-config-cli's `$(env:NAME)`, with `$(env:NAME:-default)` for a default. Keycloak's own
`${NAME}` would be imported as literal text, so the files use none.

**Changing the file later.** A new release of `rag-additions.json` goes in the same way: copy, check the env file,
`up`. With `no-delete`, a client, role or scope mapping that a new version drops stays in the realm. Remove it in the
admin console.

**Redirect URIs.** `rag-additions.json` holds only the pilot hosts. A redirect to whatever runs on a user's localhost
must not be accepted in production, so `rag-additions.local.json` never goes into the production additions
directory. In a **test** realm, put it next to `rag-additions.json`. It sorts after it, and keycloak-config-cli
replaces the two clients' URI lists with its own, which keep the pilot URIs and add these:
- `ume-rag-admin`: `https://localhost:5174` (admin UI) and `https://localhost:7402` (admin API).
- `open-webui`: `http://localhost:8080`.

**Group names (#413).** `ume-rag-admin`'s `groups` mapper sends every group of the account by its short name
(*Full group path* off, identity design §2.5), the gateway's groups included, as the gateway's own mapper does.
Group names are therefore shared across the realm:
- **Role groups** (`Oidc:RoleGroups:<role>`) grant an Ume.Rag role. Each one must be named
  `rag-…` (case-insensitive) or be an Entra object id, and the admin API refuses to start otherwise. A gateway group
  can't grant an Ume.Rag role by sharing its name. Don't give a gateway group a `rag-` name.
- **Member groups** on a department (*Organisation* page) give department scope only, never a role. They are meant to
  be the same groups the gateway uses (identity design §2.1), so a gateway group of that name counts on purpose.

**PKCE on `open-webui`** is not required yet. Requiring it in Keycloak (`pkce.code.challenge.method` `S256`) only works
once Open WebUI sends a challenge (`OAUTH_CODE_CHALLENGE_METHOD=S256`), so the two are set together. `ume-rag-admin`
already requires S256.

## Standalone

Ume.Rag can later run with its own Keycloak (Decisions "Identity providers: AD and Entra ID through Keycloak, Ume.Rag
standalone later"). Then `standalone/10-rag-realm.json` takes the place of the gateway's base file and the same
`rag-additions.json` follows, applied by the same keycloak-config-cli image with the same settings as the gateway's
`realm-import` job:

```bash
docker run --rm --network <Keycloak's network> \
  -e KEYCLOAK_URL=http://keycloak:8080 -e KEYCLOAK_GRANTTYPE=client_credentials -e KEYCLOAK_CLIENTID=<bootstrap admin client> \
  -e SPRING_CONFIG_IMPORT=configtree:/run/import-secrets/config/ \
  -v <a directory with the file keycloak.client-secret>:/run/import-secrets/config:ro \
  -e IMPORT_VARSUBSTITUTION_ENABLED=true \
  -e IMPORT_FILES_LOCATIONS=file:/config/realm/*.json,file:/config/additions/*.json \
  -e IMPORT_MANAGED_CLIENT=no-delete -e IMPORT_MANAGED_ROLE=no-delete -e IMPORT_MANAGED_SCOPEMAPPING=no-delete \
  --env-file <the placeholder values> \
  -v "$PWD/deploy/keycloak/standalone:/config/realm:ro" -v <a directory with rag-additions.json>:/config/additions:ro \
  docker.io/adorsys/keycloak-config-cli:6.5.1-26.5.5@sha256:0955d98c8a341898b7aa177477edf8a1e90569ae50bbe7598141c1270b773274
```

Set every other `IMPORT_MANAGED_*` to `no-delete` too, as the gateway's `compose.yaml` does (the full list is
`RealmImport.ImportSettings` in the tests). Standalone has no Compose project of its own yet: it is a tested realm file,
ready for one. OTP is not set here either.

## Tested

`tests/Ume.Rag.AdminApi.Tests/RealmAdditionsTests.cs` starts Keycloak 26.8.1 (the gateway's digest) and runs
keycloak-config-cli against it as the gateway's `realm-import` job does: the same image and digest, client-credentials
sign-in with the secret from a config tree, `$(env:...)` substitution and `no-delete`. The base realm is a minimal
stand-in for `realm/10-gateway.json` (`tests/Ume.Rag.AdminApi.Tests/Keycloak/10-gateway.json`: the realm settings,
the user profile, the client `ume-admin` and two gateway roles). The tests check:

- The additions import after the base realm and leave its settings, user profile and browser flow alone.
- A second import deletes nothing: not a client or role made by hand, not the base file's or RAG's clients, not users
  or their role grants.
- A user with `gateway-admin` and two `rag-*` roles gets only the `rag-*` roles plus `departmentCodes` in the
  `ume-rag-admin` token, only `gateway-admin` in the `ume-admin` token, and no roles in the `open-webui` token.
- Admins and editors sign in through the admin BFF with the password alone, without OTP.
- The additions hold only `realm`, `roles`, `clients` and `scopeMappings`, use only `$(env:...)` placeholders, and
  `rag-additions.json` holds no localhost URI.

`StandaloneRealmTests.cs` imports `standalone/10-rag-realm.json` and `rag-additions.json` into an empty Keycloak and
checks the password policy, brute-force protection, the user profile, the `ume-rag-admin` token and a clean second
import.
