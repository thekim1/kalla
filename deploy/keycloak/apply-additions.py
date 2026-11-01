#!/usr/bin/env python3
"""Apply RAG's realm additions to an existing Keycloak realm through the admin REST API (identity design §11.2).

Two parts, applied separately or together:

  additions  rag-additions.json: the rag-* realm roles and the clients ume-rag-admin, open-webui and ume-rag-api in one
             partial import with ifResourceExists=FAIL (all or nothing: an existing role or client of the same name
             aborts the import and changes nothing), then the role scope mappings of ume-rag-admin (full scope is off).
  settings   rag-realm-settings.json: realm-wide settings shared with the gateway: the user-profile attributes
             departmentCodes and teamCodes (added if missing, kept if already declared), the browser flow rag-browser
             (OTP for RAG's admins; refused if a flow of that name exists), then password policy, brute-force
             protection, OTP policy and the browser-flow binding. Prints each realm value it changes.

Placeholders ${NAME} and ${NAME:default} in the files are replaced from the environment, as Keycloak's start-up import
does; a placeholder without a value stops the script before any call.

Usage:
  KEYCLOAK_ADMIN_PASSWORD=... UME_RAG_ADMIN_PUBLIC_HOST=... UME_RAG_OPENWEBUI_HOST=... \\
  UME_RAG_OIDC_CLIENT_SECRET=... UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=... \\
    python3 -I deploy/keycloak/apply-additions.py --url https://keycloak.example --realm ume --admin-user admin \\
      [--part additions|settings|all]

Standard library only. Exit code 0 on success, 1 on a refused or failed step (the message says which).
"""

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PLACEHOLDER = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::([^}]*))?\}")


class Refused(Exception):
    pass


def substitute(text):
    missing = []

    def value(match):
        name, default = match.group(1), match.group(2)
        found = os.environ.get(name, default)
        if found is None:
            missing.append(name)
            return match.group(0)
        return json.dumps(found)[1:-1]  # inside a JSON string

    result = PLACEHOLDER.sub(value, text)
    if missing:
        raise Refused("no value for " + ", ".join(sorted(set(missing))) + " in the environment")
    return result


def load(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as f:
        return json.loads(substitute(f.read()))


class Admin:
    def __init__(self, url, realm, admin_realm, user, password):
        self.base = url.rstrip("/")
        self.realm = realm
        token = self._send("POST", f"/realms/{q(admin_realm)}/protocol/openid-connect/token",
                           form={"grant_type": "password", "client_id": "admin-cli", "username": user, "password": password},
                           auth=False)
        self.token = token["access_token"]

    def _send(self, method, path, body=None, form=None, auth=True, ok=(200, 201, 204)):
        data, headers = None, {"Accept": "application/json"}
        if form is not None:
            data = urllib.parse.urlencode(form).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        elif body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        if auth:
            headers["Authorization"] = "Bearer " + self.token
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read()
                if response.status not in ok:
                    raise Refused(f"{method} {path}: {response.status}")
                location = response.headers.get("Location")
                if raw:
                    return json.loads(raw)
                return {"location": location} if location else None
        except urllib.error.HTTPError as e:
            raise Refused(f"{method} {path}: {e.code} {e.read().decode(errors='replace')[:500]}") from None

    def call(self, method, path, body=None):
        return self._send(method, f"/admin/realms/{q(self.realm)}{path}", body=body)


def q(value):
    return urllib.parse.quote(value, safe="")


def apply_additions(admin, additions):
    print(f"additions: {len(additions['roles']['realm'])} realm roles, clients "
          + ", ".join(c["clientId"] for c in additions["clients"]))
    result = admin.call("POST", "/partialImport", {
        "ifResourceExists": "FAIL",
        "roles": additions["roles"],
        "clients": additions["clients"],
    })
    print(f"  partial import: {result.get('added', '?')} added")
    for mapping in additions.get("scopeMappings", []):
        clients = admin.call("GET", "/clients?clientId=" + q(mapping["client"]))
        if len(clients) != 1:
            raise Refused(f"scope mappings: client {mapping['client']} not found")
        roles = [admin.call("GET", "/roles/" + q(name)) for name in mapping["roles"]]
        admin.call("POST", f"/clients/{clients[0]['id']}/scope-mappings/realm", roles)
        print(f"  scope mappings of {mapping['client']}: " + ", ".join(mapping["roles"]))


def apply_settings(admin, settings):
    # 1. User profile: shared attributes, declared once for both systems.
    profile = admin.call("GET", "/users/profile")
    names = {a["name"] for a in profile.get("attributes", [])}
    added = [a for a in settings["userProfileAttributes"] if a["name"] not in names]
    kept = [a["name"] for a in settings["userProfileAttributes"] if a["name"] in names]
    if added:
        profile["attributes"] = profile.get("attributes", []) + added
        admin.call("PUT", "/users/profile", profile)
    print("settings: user profile: added " + (", ".join(a["name"] for a in added) or "none")
          + "; already declared " + (", ".join(kept) or "none"))

    # 2. Authentication flows (top-level first, sub-flows created where they are referenced).
    flows = {f["alias"]: f for f in settings["authenticationFlows"]}
    configs = {c["alias"]: c for c in settings["authenticatorConfig"]}
    existing = {f["alias"] for f in admin.call("GET", "/authentication/flows")}
    for flow in (f for f in flows.values() if f["topLevel"]):
        if flow["alias"] in existing:
            raise Refused(f"flow {flow['alias']} already exists")
        admin.call("POST", "/authentication/flows", {
            "alias": flow["alias"], "description": flow["description"], "providerId": flow["providerId"],
            "topLevel": True, "builtIn": False,
        })
        build_flow(admin, flow, flows, configs)
        print(f"  flow {flow['alias']} created")

    # 3. Realm values, the browser-flow binding last (its flow exists now).
    current = admin.call("GET", "")
    changes = {k: v for k, v in settings["realm"].items() if current.get(k) != v}
    for key, value in changes.items():
        print(f"  realm {key}: {json.dumps(current.get(key))} -> {json.dumps(value)}")
    if changes:
        admin.call("PUT", "", changes)


def build_flow(admin, flow, flows, configs):
    alias = flow["alias"]
    executions = sorted(flow["authenticationExecutions"], key=lambda e: e["priority"])
    for execution in executions:
        if execution["authenticatorFlow"]:
            sub = flows[execution["flowAlias"]]
            admin.call("POST", f"/authentication/flows/{q(alias)}/executions/flow", {
                "alias": sub["alias"], "description": sub["description"], "type": sub["providerId"],
                "provider": "registration-page-form",
            })
        else:
            admin.call("POST", f"/authentication/flows/{q(alias)}/executions/execution", {"provider": execution["authenticator"]})

    created = [e for e in admin.call("GET", f"/authentication/flows/{q(alias)}/executions") if e["level"] == 0]
    if len(created) != len(executions):
        raise Refused(f"flow {alias}: {len(created)} executions after creation, expected {len(executions)}")
    for want, have in zip(executions, created):
        have["requirement"] = want["requirement"]
        admin.call("PUT", f"/authentication/flows/{q(alias)}/executions", have)
        if "authenticatorConfig" in want:
            config = configs[want["authenticatorConfig"]]
            admin.call("POST", f"/authentication/executions/{have['id']}/config", {"alias": config["alias"], "config": config["config"]})
        if want["authenticatorFlow"]:
            build_flow(admin, flows[want["flowAlias"]], flows, configs)


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--url", required=True, help="Keycloak's base URL, e.g. https://keycloak.example")
    parser.add_argument("--realm", default="ume")
    parser.add_argument("--admin-realm", default="master")
    parser.add_argument("--admin-user", required=True)
    parser.add_argument("--part", choices=["additions", "settings", "all"], default="all")
    args = parser.parse_args()
    password = os.environ.get("KEYCLOAK_ADMIN_PASSWORD")
    if not password:
        print("refused: set KEYCLOAK_ADMIN_PASSWORD", file=sys.stderr)
        return 1
    try:
        additions = load("rag-additions.json") if args.part in ("additions", "all") else None
        settings = load("rag-realm-settings.json") if args.part in ("settings", "all") else None
        admin = Admin(args.url, args.realm, args.admin_realm, args.admin_user, password)
        if additions is not None:
            apply_additions(admin, additions)
        if settings is not None:
            apply_settings(admin, settings)
    except Refused as e:
        print(f"refused: {e}", file=sys.stderr)
        return 1
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
