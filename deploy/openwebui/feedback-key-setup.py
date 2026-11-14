#!/usr/bin/env python3
"""The feedback service account's Open WebUI API key, minted without password sign-in (M-17 C6).

rag-api's feedback import (chat design §13.3) reads Open WebUI's feedback list with this key. Open WebUI's password
sign-in stays off, so this one-shot job, run in Open WebUI's own image after it starts, mints the key the way Open WebUI's
own session does it:

  1. the service account's id and session stamp from Open WebUI's SQLite store, read-only (by WEBUI_ADMIN_EMAIL);
  2. a 5-minute session JWT (HS256, {id, session_stamp, typ: session}) signed with Open WebUI's WEBUI_SECRET_KEY;
  3. POST /api/v1/auths/api_key with it, which mints the account's `sk-` key (a new one replaces the old);
  4. the key is checked on the feedback list (API_KEYS_ALLOWED_ENDPOINTS limits it to the feedback routes) and written
     as OpenWebUi__AdminApiKey into the directory rag-api reads it from (mode 0640; rag-api reloads it).

A key that still works is kept, so the job runs on every deploy. A new key: delete the file and deploy again.
Checked against Open WebUI v0.12.5 (the spike on #667): the claims and tables are Open WebUI's internals, so an Open WebUI
upgrade re-runs the boot test (deploy/tests/portainer-boot.sh) before its pin moves.

Environment:
  WEBUI_ADMIN_EMAIL         the service account (Open WebUI creates it on an empty store from the same variable)
  OWUI_URL                  where this job reaches Open WebUI       [http://rag-openwebui:8080]
  OWUI_DB                   Open WebUI's SQLite store                [/data/webui.db]
  OWUI_SECRET_KEY_FILE      Open WebUI's WEBUI_SECRET_KEY            [/run/owui-secrets/webui_secret_key]
  FEEDBACK_KEY_FILE         where the key goes                       [/run/feedback/OpenWebUi__AdminApiKey]
  OWUI_WAIT_SECONDS         how long to wait for Open WebUI to start [600]
"""

import base64
import hashlib
import hmac
import json
import os
import sqlite3
import sys
import time
import urllib.error
import urllib.request
import uuid

URL = os.environ.get("OWUI_URL", "http://rag-openwebui:8080").rstrip("/")
DB = os.environ.get("OWUI_DB", "/data/webui.db")
SECRET_FILE = os.environ.get("OWUI_SECRET_KEY_FILE", "/run/owui-secrets/webui_secret_key")
KEY_FILE = os.environ.get("FEEDBACK_KEY_FILE", "/run/feedback/OpenWebUi__AdminApiKey")
WAIT = int(os.environ.get("OWUI_WAIT_SECONDS", "600"))
FEEDBACK_LIST = "/api/v1/evaluations/feedbacks/list"


def log(message):
    print(f"rag-openwebui-setup: {message}", flush=True)


def fail(message):
    log(f"error: {message}")
    sys.exit(1)


def request(method, path, token):
    """The HTTP status and body of one call to Open WebUI with a bearer token."""
    req = urllib.request.Request(URL + path, method=method, headers={"Authorization": f"Bearer {token}"})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def works(key):
    return request("GET", FEEDBACK_LIST, key)[0] == 200


def b64(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=")


def session_token(user_id, stamp, secret):
    """An HS256 session JWT as Open WebUI's create_token writes it (utils/auth.py), valid for 5 minutes."""
    now = int(time.time())
    header = b64(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    claims = {"id": user_id, "session_stamp": stamp, "typ": "session", "auth_method": "password", "iat": now,
              "exp": now + 300, "jti": str(uuid.uuid4())}
    payload = b64(json.dumps(claims, separators=(",", ":")).encode())
    signature = b64(hmac.new(secret.encode(), header + b"." + payload, hashlib.sha256).digest())
    return (header + b"." + payload + b"." + signature).decode()


def wait_for_open_webui():
    deadline = time.monotonic() + WAIT
    while True:
        try:
            with urllib.request.urlopen(URL + "/health", timeout=10) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, OSError):
            pass
        if time.monotonic() > deadline:
            fail(f"Open WebUI did not answer {URL}/health within {WAIT} s")
        time.sleep(5)


def account(email):
    """The service account's id and session stamp, read-only; the store's WAL needs a writable directory, not a write."""
    with sqlite3.connect(f"file:{DB}?mode=ro", uri=True, timeout=30) as db:
        row = db.execute(
            'SELECT u.id, u.role, a.session_stamp FROM "user" u JOIN auth a ON a.id = u.id WHERE lower(u.email) = lower(?)',
            (email,),
        ).fetchone()
    if row is None:
        fail(f"no Open WebUI account {email}: Open WebUI creates it from WEBUI_ADMIN_EMAIL only on an empty store")
    if row[1] != "admin":
        fail(f"the Open WebUI account {email} has the role {row[1]}, not admin")
    return row[0], row[2]


def write_key(key):
    directory = os.path.dirname(KEY_FILE)
    # KeyPerFile skips the "ignore." prefix, so rag-api never reads the half-written file.
    temporary = os.path.join(directory, f"ignore.{os.path.basename(KEY_FILE)}.tmp")
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o640)
    with os.fdopen(fd, "w") as file:
        file.write(key)
    os.chmod(temporary, 0o640)
    os.replace(temporary, KEY_FILE)


def main():
    email = os.environ.get("WEBUI_ADMIN_EMAIL", "").strip()
    if not email:
        fail("WEBUI_ADMIN_EMAIL is not set")
    wait_for_open_webui()
    if os.path.exists(KEY_FILE):
        with open(KEY_FILE) as file:
            current = file.read().strip()
        if current and works(current):
            log("the feedback key works; kept")
            return
        log("the feedback key is refused; minting a new one")
    with open(SECRET_FILE) as file:
        secret = file.read().strip()
    if not secret:
        fail(f"{SECRET_FILE} is empty")
    user_id, stamp = account(email)
    status, body = request("POST", "/api/v1/auths/api_key", session_token(user_id, stamp, secret))
    if status != 200:
        fail(f"Open WebUI refused to mint the key (HTTP {status}): {body[:200].decode(errors='replace')}")
    key = json.loads(body).get("api_key") or ""
    if not key.startswith("sk-"):
        fail("Open WebUI answered without an sk- key")
    if not works(key):
        fail("the new key is refused on the feedback list: check API_KEYS_ALLOWED_ENDPOINTS")
    write_key(key)
    log(f"minted the feedback key for {email}; rag-api reads it within seconds")


if __name__ == "__main__":
    main()
