# Entra approval package 1

RAG's own Entra objects for admin and Open WebUI sign-in, as Bicep with the Microsoft Graph extension
([`plans/identity/design.md`](../../plans/identity/design.md) §12), applied by
[`.github/workflows/entra.yml`](../../.github/workflows/entra.yml). It never declares or changes the gateway's
registration, and it requests no Graph application permission for RAG.

## What it contains

| Object | File | Content |
|---|---|---|
| `ume-rag-admin` registration | `admin.bicep` | single tenant; Web redirect URIs `https://<rag admin host>/signin-oidc` and `/signout-callback-oidc`; app roles `rag-platform-admin`, `rag-department-admin`, `rag-pipeline-editor`, `rag-reviewer`, `rag-viewer`, `rag-editor` (P2), `rag-skill-editor` (P2) for users and groups; `groupMembershipClaims = SecurityGroup`; optional ID-token claims `email`, `acct`; `acceptMappedClaims` for the `departmentCodes` claim (parameter, default on, as the gateway's Entra step 4); delegated Microsoft Graph `openid profile email` |
| its service principal | `admin.bicep` | `appRoleAssignmentRequired = true` |
| `ume-rag-open-webui` registration | `open-webui.bicep` | single tenant; Web redirect URI `https://<open webui host>/oauth/oidc/login/callback`; optional ID-token claims `email`, `acct`; delegated Microsoft Graph `openid profile email` |
| its service principal | `open-webui.bicep` | `appRoleAssignmentRequired = true` |
| security groups | `main.bicep` | `Ume.Rag pilot users`, `Ume.Rag platform admins`, `Ume.Rag department admins – <pilot department>` (names to agree with IT) |

App role ids are derived from the role values (`guid('ume-rag-admin', value)`), so they never change between runs. The
identity that applies the templates is made an owner of every object, so it can update them on the next run. Package 2
(`rag-api`, `rag-ingestion`, `ume-rag-tools`, the MCP server registrations) is not here.

A deployment replaces whole arrays (`appRoles`, `redirectUris`, `optionalClaims`): a change made by hand in the portal
to one of these registrations is overwritten on the next apply, and the plan shows it first. To retire an app role,
deploy it with `isEnabled: false` first; only then can it be removed.

## What is not in Bicep (manual steps)

The Graph extension cannot create client secrets (`passwordCredentials`), and the deployer is not allowed to consent or
assign roles. These are the approval itself and are done by people after an apply; the run's summary prints the commands
with the object ids filled in (`entra.py next-steps`):

1. **Admin consent** for `openid profile email` on both registrations (an Entra administrator):
   `az ad app permission admin-consent --id <appId>`.
2. **Group → app role assignments**: platform admins group → `rag-platform-admin` and department admins group →
   `rag-department-admin` on `ume-rag-admin`; pilot users group → default access on `ume-rag-open-webui`; reviewer
   groups → `rag-reviewer` when the department has them. Needs `AppRoleAssignment.ReadWrite.All` or ownership of the
   registration.
3. **Client secrets**, one per registration, by an operator: `az ad app credential reset --id <appId> --append`. The
   admin secret goes to `init-deployment.sh --oidc-client-secret-file` (`Oidc__ClientSecret`), the Open WebUI secret to
   `OAUTH_CLIENT_SECRET`. Never in a workflow, never in the repository.
4. **Group membership** of the pilot groups: maintained by IT, not by IaC.
5. **`departmentCodes` claim** on `ume-rag-admin`: a claims-mapping policy from the user attribute that holds the
   cost-centre code (the gateway's `docs/identity-and-roles.md`, Entra ID step 4), by IT.

## Identities and settings (one-time, part of the approval)

| What | Where | Content |
|---|---|---|
| Deployer `ume-rag-entra-deploy` | Entra app registration | federated credentials for this repository with subjects `repo:<owner>/<repo>:environment:entra-test` and `…:environment:entra-prod`; Graph application permissions `Application.ReadWrite.OwnedBy` and `Group.Create` (admin consent); no `AppRoleAssignment.ReadWrite.All`, no directory role |
| Plan identity `ume-rag-entra-plan` | Entra app registration | federated credential `repo:<owner>/<repo>:environment:entra-plan`; Graph application permission `Application.Read.All` (read only) |
| Deployment record | Azure | a subscription and an (empty) resource group; the deployer may create deployments there (for example *Contributor* on that resource group only) |
| `ENTRA_TENANT_ID`, `ENTRA_PLAN_CLIENT_ID` | GitHub repository variables | the tenant id and the plan identity's application id; the plan is skipped until both are set |
| `entra-plan` | GitHub environment | no reviewers |
| `entra-test`, `entra-prod` | GitHub environments | required reviewers = the municipality's approvers; deployment branches: `main` only; variables `ENTRA_DEPLOY_CLIENT_ID`, `ENTRA_SUBSCRIPTION_ID`, `ENTRA_RESOURCE_GROUP` |
| `test.bicepparam`, `prod.bicepparam` | this directory | copies of `sample.bicepparam` with the real host names and pilot department (no secrets) |

## How it runs

- **Pull request** touching `infra/entra/**` or the workflow: Bicep build, lint and format check of every file, without
  credentials. With the plan identity configured, the plan job compares the desired state (`bicep snapshot`) with the
  live registrations and service principals and posts it as a PR comment. What-if is not supported for Graph
  resources, so this comparison stands in for it. Groups are listed, not compared (the plan identity cannot read
  groups).
- **Apply**: *Actions → Entra → Run workflow* on `main`, environment `entra-test` or `entra-prod`. The run shows the plan
  for that environment's parameter file, then waits for the environment's reviewers; after approval it runs
  `az deployment group create` with the built template and prints the manual steps above in the run summary.

## Submitting package 1

The user submits it to the municipality's Entra administrators (IT), through their change process:

1. Agree the group names and the host names with IT; add `test.bicepparam` (and later `prod.bicepparam`) in a pull
   request, whose plan shows the result.
2. Ask IT to create the two identities, the resource group and the GitHub settings above, and to name the approvers.
3. Start the apply run; it waits for the approvers. Record the change ticket id and the run in P0 checklist item 17.
4. After approval, IT makes the manual steps 1–5; the operator creates the client secrets.

## Running it locally

Bicep CLI v0.48.1 (the version `entra.yml` pins), from `infra/entra/`:

```bash
for f in *.bicep; do bicep build "$f" --stdout > /dev/null && bicep lint "$f"; done
bicep build-params sample.bicepparam --stdout > /dev/null
# Desired state, resolved offline (writes sample.snapshot.json next to the parameter file; do not commit it):
bicep snapshot sample.bicepparam --mode overwrite
# Desired versus live, with your own az login (needs read access to applications):
az login --allow-no-subscriptions
python3 -I entra.py plan sample.snapshot.json
```

A deployment from a workstation (`az deployment group create -g <resource group> -f main.bicep -p test.bicepparam`) is
only for a test tenant; the municipality's tenant is changed through `entra.yml`.
