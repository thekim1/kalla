"""Plan and follow-up output for entra.yml (plans/identity/design.md §12.4).

Usage: python3 -I infra/entra/entra.py plan <snapshot.json>
       python3 -I infra/entra/entra.py next-steps <outputs.json>

plan: what-if does not support Microsoft Graph resources, so this compares the desired state from `bicep snapshot` with
the live applications and service principals (`az rest`, read-only) and prints the difference as Markdown. Groups are
listed, not compared (the plan identity has Application.Read.All only), and so are the owners relationships.

next-steps: prints the manual steps that remain after an apply (§12.3, §12.4 step 4), with the object ids filled in
from the deployment outputs (`az deployment group create --query properties.outputs`).
"""

import json
import subprocess
import sys

GRAPH = "https://graph.microsoft.com/v1.0"
APPLICATION = "Microsoft.Graph/applications@v1.0"
SERVICE_PRINCIPAL = "Microsoft.Graph/servicePrincipals@v1.0"
GROUP = "Microsoft.Graph/groups@v1.0"
# Relationships are not returned with the object; the deployer is the only owner the templates set.
NOT_COMPARED = {"owners", "members"}


def graph_get(path: str) -> dict | None:
    """GET a Graph object; None when it does not exist."""
    run = subprocess.run(
        ["az", "rest", "--method", "get", "--url", f"{GRAPH}/{path}"], capture_output=True, text=True, check=False
    )
    if run.returncode == 0:
        return json.loads(run.stdout)
    if "Request_ResourceNotFound" in run.stderr or "Not Found" in run.stderr:
        return None
    raise SystemExit(f"az rest GET {path} failed:\n{run.stderr}")


def unresolved(value: object) -> bool:
    """An ARM expression the snapshot cannot evaluate offline, such as deployer() or reference()."""
    return isinstance(value, str) and value.startswith("[") and value.endswith("]")


def normalise(value: object) -> object:
    """Graph does not keep the order of collections, so nested arrays are compared sorted."""
    if isinstance(value, dict):
        return {k: normalise(v) for k, v in value.items()}
    if isinstance(value, list):
        return sorted((normalise(v) for v in value), key=lambda v: json.dumps(v, sort_keys=True))
    return value


def canonical(value: object, keys: set[str] | None) -> str:
    if isinstance(value, dict) and keys is not None:
        value = {k: value.get(k) for k in keys}
    return json.dumps(normalise(value), sort_keys=True, ensure_ascii=False)


def diff(desired: object, live: object, path: str, out: list[str]) -> None:
    """Appends one line per difference. Only what the template declares is compared; arrays are unordered sets, and
    their items are compared on the keys the template declares (a deployment replaces the whole array, so an item
    that exists only live is removed)."""
    if unresolved(desired):
        return
    if isinstance(desired, dict):
        live = live if isinstance(live, dict) else {}
        for key, value in desired.items():
            if key not in NOT_COMPARED:
                diff(value, live.get(key), f"{path}.{key}" if path else key, out)
        return
    if isinstance(desired, list):
        live = live if isinstance(live, list) else []
        keys = {k for item in desired if isinstance(item, dict) for k in item} or None
        want = {canonical(item, keys) for item in desired}
        have = {canonical(item, keys) for item in live}
        out.extend(f"`{path}` + {item}" for item in sorted(want - have))
        out.extend(f"`{path}` − {item}" for item in sorted(have - want))
        return
    if desired != live:
        out.append(f"`{path}`: {json.dumps(live, ensure_ascii=False)} → {json.dumps(desired, ensure_ascii=False)}")


def section(title: str, changes: list[str] | None) -> list[str]:
    if changes is None:
        return [f"- **{title}**: create"]
    if not changes:
        return [f"- **{title}**: no change"]
    return [f"- **{title}**: update"] + [f"  - {line}" for line in changes]


def plan(snapshot_path: str) -> None:
    with open(snapshot_path, encoding="utf-8") as f:
        resources = json.load(f)["predictedResources"]
    lines: list[str] = []
    live_app: dict | None = None
    app_name = ""
    for resource in resources:
        kind, props = resource["type"], resource["properties"]
        if kind == APPLICATION:
            app_name = props["uniqueName"]
            live_app = graph_get(f"applications(uniqueName='{app_name}')")
            changes = None if live_app is None else []
            if live_app is not None:
                diff(props, live_app, "", changes)
            lines += section(f"application `{app_name}`", changes)
        elif kind == SERVICE_PRINCIPAL:
            # Each module declares its application first and then the application's service principal, whose appId is
            # a reference() the snapshot cannot resolve; it is looked up through the live application instead.
            if not app_name:
                raise SystemExit("a service principal precedes its application in the snapshot")
            live_sp = None if live_app is None else graph_get(f"servicePrincipals(appId='{live_app['appId']}')")
            changes = None if live_sp is None else []
            if live_sp is not None:
                diff(props, live_sp, "", changes)
            lines += section(f"service principal of `{app_name}`", changes)
        elif kind == GROUP:
            lines.append(f"- **group `{props['uniqueName']}`** ({props['displayName']}): declared, not compared")
        else:
            raise SystemExit(f"unexpected resource type {kind}")
    lines.append("")
    lines.append("Owners are not compared. A registration that was made by hand without a `uniqueName` shows as create.")
    print("\n".join(lines))


def next_steps(outputs_path: str) -> None:
    with open(outputs_path, encoding="utf-8") as f:
        outputs = {k: v["value"] for k, v in json.load(f).items()}
    admin_sp = outputs["adminServicePrincipalId"]
    owui_sp = outputs["openWebUiServicePrincipalId"]
    groups = {g["key"]: g for g in outputs["groups"]}
    roles = {r["value"]: r["id"] for r in outputs["adminAppRoles"]}

    def assign(sp: str, group: str, role: str) -> str:
        body = json.dumps({"principalId": groups[group]["id"], "resourceId": sp, "appRoleId": role})
        return (
            f"az rest --method post --url '{GRAPH}/servicePrincipals/{sp}/appRoleAssignedTo' "
            f"--headers content-type=application/json --body '{body}'"
        )

    print(
        f"""### Still needs a person (plans/identity/design.md §12.3)

Applied: `ume-rag-admin` (appId `{outputs["adminAppId"]}`), `ume-rag-open-webui` (appId `{outputs["openWebUiAppId"]}`),
groups {", ".join(f"`{g['uniqueName']}` (`{g['id']}`)" for g in outputs["groups"])}.

1. **Admin consent** for `openid profile email` on both registrations (an Entra administrator):
   ```
   az ad app permission admin-consent --id {outputs["adminAppId"]}
   az ad app permission admin-consent --id {outputs["openWebUiAppId"]}
   ```
2. **Group → app role assignments** (needs ownership of the app or `AppRoleAssignment.ReadWrite.All`):
   ```
   {assign(admin_sp, "platformAdmins", roles["rag-platform-admin"])}
   {assign(admin_sp, "departmentAdmins", roles["rag-department-admin"])}
   {assign(owui_sp, "pilotUsers", "00000000-0000-0000-0000-000000000000")}
   ```
   Reviewer, editor and viewer groups are assigned the same way when they exist (role ids: {", ".join(
            f"`{v}` {k}" for k, v in roles.items())}).
3. **Client secrets**, one per registration, by an operator; never in this workflow (§12.2):
   `az ad app credential reset --id <appId> --append --display-name <deployment> --years 1`, then
   `init-deployment.sh --oidc-client-secret-file` for `ume-rag-admin`, `OAUTH_CLIENT_SECRET` for Open WebUI.
4. **Group membership** of the pilot groups: maintained by IT.
5. **`departmentCodes` claim** on `ume-rag-admin` (the gateway's Entra step 4): claims-mapping policy by IT.
"""
    )


def main() -> None:
    if len(sys.argv) != 3 or sys.argv[1] not in ("plan", "next-steps"):
        raise SystemExit(__doc__)
    (plan if sys.argv[1] == "plan" else next_steps)(sys.argv[2])


if __name__ == "__main__":
    main()
