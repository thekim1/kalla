# Keycloak

Ume Rag's admin UI signs in with OIDC (`Oidc:*`, client `ume-rag-admin`). Any OIDC provider works; this page covers
Keycloak, which the gateway can run for both products. Keycloak itself (the Compose project, production settings,
two-factor sign-in, brokering Entra ID or AD, accounts, backup) is operated as in the gateway's
[Keycloak guide](https://github.com/thekim1/llmgateway/blob/main/docs/keycloak.md): Ume Rag adds realm files to it, it
does not run its own. The realm files and their placeholders are described in
[`deploy/keycloak/README.md`](../deploy/keycloak/README.md).

| Your setup | What to do |
|---|---|
| **The gateway's Keycloak, shared realm `ume`** (the pilot, Decisions "Gateway release 1 and shared Keycloak as built") | Copy `deploy/keycloak/rag-additions.json` from the Ume Rag release into the gateway's Keycloak additions directory, set its placeholders, and run the gateway's Keycloak `up` again (README, *Shared realm*). Then rag-admin's setup wizard takes the realm `https://<keycloak host>/realms/ume` and the client secret (Compose with `--hardened`: also `init-deployment.sh --oidc-authority https://<keycloak host>/realms/ume`, for the host firewall) |
| **Gateway and RAG in one Portainer stack** | The same additions file in the gateway's Keycloak; in the stack, `UME_RAG_OIDC_AUTHORITY` defaults to the gateway's `UME_OIDC_AUTHORITY`, and without either rag-admin's setup wizard suggests the realm saved in the gateway's wizard (runbook, *Portainer and other hosts without shell access*) |
| **A Keycloak of RAG's own** (standalone) | `deploy/keycloak/standalone/10-rag-realm.json` in place of the gateway's base realm file, then `rag-additions.json`, applied with keycloak-config-cli (README, *Standalone*). Run Keycloak with the gateway's `deploy/keycloak` project |
| **Entra ID or AD FS directly**, no Keycloak | Register `ume-rag-admin` there and enter its authority in rag-admin's setup wizard (Compose with `--hardened`: also `init-deployment.sh --oidc-authority`); Entra needs its tenant ID, in the wizard or as `UME_RAG_OIDC_TENANT_ID` (production setup, *Sign-in*) |
| **Kubernetes** | Any of the above; the overlay's `Oidc__Authority` names the realm (Kubernetes guide) |

In every setup:

- **Redirect URIs** of `ume-rag-admin`: `https://<admin host>/signin-oidc` and `https://<admin host>/signout-callback-oidc`
  (with the port, when the admin is reached on 9543 directly). `rag-additions.json` takes them from
  `UME_RAG_ADMIN_PUBLIC_HOST`. `rag-additions.local.json` adds localhost URIs and never goes into production.
- **The client secret** must be the same in Keycloak and in RAG: entered in rag-admin's setup wizard, or declared by
  the deployment (`UME_RAG_OIDC_CLIENT_SECRET`: `init-deployment.sh --set-file UME_RAG_OIDC_CLIENT_SECRET=FILE` on a
  Compose host, a variable in the Portainer stack).
- **Roles** come from the `rag-*` realm roles or from groups mapped under *Settings → Role groups* in the admin UI (and the break-glass
  `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN`); one
  group can grant a role in both products (README, *One group for both products*;
  [`examples/site-groups.json`](../deploy/keycloak/examples/site-groups.json)).
- **Two-factor sign-in** is not enforced by RAG's realm files: it is the gateway Keycloak's setting (its guide,
  *Two-factor sign-in*), or the brokered Entra ID's.

Ports and flows between the admin, the browser and Keycloak: [network-and-ports.md](network-and-ports.md), *Sign-in*.
