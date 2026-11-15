#!/usr/bin/env python3
"""Open WebUI's start with its sign-in settings from rag-api (identity design §19, "Open WebUI").

Open WebUI reads its OIDC settings only from its environment, and ENABLE_PERSISTENT_CONFIG=false makes that environment the
truth on every start. The settings live in rag-api (the deployment's OpenWebUi:Oidc:*, else the setup wizard's), so this
wrapper is the container's entrypoint:

  1. with --secrets-dir, the user-JWT secret, the feedback service account's password and the key for rag-api are read from
     their files into the environment (FORWARD_USER_INFO_HEADER_JWT_SECRET, WEBUI_ADMIN_PASSWORD, OPENAI_API_KEYS); without
     it they are already in the environment (Compose's env file);
  2. GET rag-api's internal route with that key (TLS verified against SSL_CERT_FILE when set), for up to
     UME_RAG_SIGN_IN_WAIT seconds while rag-api starts;
  3. configured: ENABLE_OAUTH_SIGNUP=true, OAUTH_CLIENT_ID, OPENID_PROVIDER_URL, OAUTH_PROVIDER_NAME, and OAUTH_CLIENT_SECRET
     (a public client without one gets OAUTH_CODE_CHALLENGE_METHOD=S256, which Open WebUI v0.12.5 requires instead of a
     secret, config.py load_oauth_providers). Not configured, or no answer: ENABLE_OAUTH_SIGNUP=false and no provider, so
     Open WebUI offers no sign-in (password sign-in stays off) and nobody can sign in. Any of these variables set from
     outside is dropped first;
  4. Open WebUI starts as a child (the command after --, default: bash start.sh); SIGTERM and SIGINT are passed to it, and
     the wrapper exits with its exit code;
  5. every UME_RAG_SIGN_IN_POLL seconds it asks again. A changed answer stops the child and exits 3, so the container's
     restart policy starts it again with the new values. A failed call changes nothing; a start without an answer
     restarts only when an answer turns out configured.

Nothing of the answer is ever written to the output: no value, no secret, no response body.

Environment:
  UME_RAG_SIGN_IN_URL    rag-api's route         [https://rag-api:8443/internal/openwebui/sign-in]
  UME_RAG_SIGN_IN_WAIT   seconds to wait at start [60]
  UME_RAG_SIGN_IN_POLL   seconds between calls    [60]
  SSL_CERT_FILE          the CA bundle that verifies rag-api (in the Portainer stack, RAG's CA among the public ones)
"""

import json
import os
import signal
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request

URL = os.environ.get("UME_RAG_SIGN_IN_URL", "https://rag-api:8443/internal/openwebui/sign-in")
WAIT = int(os.environ.get("UME_RAG_SIGN_IN_WAIT", "60"))
POLL = int(os.environ.get("UME_RAG_SIGN_IN_POLL", "60"))
SECRET_FILES = {
    "FORWARD_USER_INFO_HEADER_JWT_SECRET": "jwt-secret",
    "WEBUI_ADMIN_PASSWORD": "admin-password",
    "OPENAI_API_KEYS": "rag-api-key",
}
SIGN_IN_VARIABLES = ("ENABLE_OAUTH_SIGNUP", "OAUTH_CLIENT_ID", "OAUTH_CLIENT_SECRET", "OAUTH_CODE_CHALLENGE_METHOD",
                     "OPENID_PROVIDER_URL", "OAUTH_PROVIDER_NAME")
CHANGED_EXIT = 3
MAX_ANSWER = 64 * 1024


def log(message):
    print(f"rag-openwebui: {message}", flush=True)


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect is a failed call: the key is never sent anywhere else."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def read_secrets(environment, directory):
    """The secret files of --secrets-dir into the environment, as the stack's former shell wrapper read them."""
    for name, file in SECRET_FILES.items():
        with open(os.path.join(directory, file), encoding="utf-8") as handle:
            environment[name] = handle.read().strip()


def parse(body):
    """rag-api's answer, checked; ValueError when it is not one."""
    answer = json.loads(body)
    if not isinstance(answer, dict) or not isinstance(answer.get("configured"), bool):
        raise ValueError("no configured member")
    version = answer.get("version")
    if not isinstance(version, str) or not 0 < len(version) <= 200:
        raise ValueError("no version")
    if answer["configured"]:
        url = answer.get("provider_url")
        if not isinstance(url, str) or not url.startswith(("https://", "http://")):
            raise ValueError("no provider address")
        for name in ("client_id", "provider_name"):
            if not isinstance(answer.get(name), str) or not answer[name]:
                raise ValueError(f"no {name}")
        if answer.get("client_secret") is not None and not isinstance(answer["client_secret"], str):
            raise ValueError("client_secret is not text")
    return answer


def fetch(key, context):
    """The parsed answer, or None when rag-api could not be asked or did not answer well."""
    request = urllib.request.Request(URL, headers={"Authorization": f"Bearer {key}", "Accept": "application/json"})
    opener = urllib.request.build_opener(_NoRedirect(), urllib.request.HTTPSHandler(context=context))
    try:
        with opener.open(request, timeout=10) as response:
            return parse(response.read(MAX_ANSWER + 1)[:MAX_ANSWER])
    except (urllib.error.URLError, OSError, ValueError):
        return None


def sign_in_environment(environment, answer):
    """The child's environment: the sign-in variables from the answer, none from outside."""
    child = {k: v for k, v in environment.items() if k not in SIGN_IN_VARIABLES}
    if answer is None or not answer["configured"]:
        child["ENABLE_OAUTH_SIGNUP"] = "false"
        return child
    child.update({
        "ENABLE_OAUTH_SIGNUP": "true",
        "OAUTH_CLIENT_ID": answer["client_id"],
        "OPENID_PROVIDER_URL": answer["provider_url"],
        "OAUTH_PROVIDER_NAME": answer["provider_name"],
    })
    if answer.get("client_secret"):
        child["OAUTH_CLIENT_SECRET"] = answer["client_secret"]
    else:
        child["OAUTH_CODE_CHALLENGE_METHOD"] = "S256"
    return child


def changed(started, answer):
    """Whether a new answer means a restart: another version, or the first answer after a start without one, if configured."""
    return answer["configured"] if started is None else answer["version"] != started["version"]


def exit_code(returncode):
    """A child ended by a signal exits as a shell reports it (128 + the signal)."""
    return 128 - returncode if returncode < 0 else returncode


def self_test():
    """The pure parts, without rag-api or Open WebUI: --self-test."""
    on = parse(b'{"configured":true,"provider_url":"https://idp/x","client_id":"c","client_secret":"s","provider_name":"N","version":"v1"}')
    public = dict(on, client_secret=None)
    off = parse(b'{"configured":false,"provider_url":null,"client_id":null,"client_secret":null,"provider_name":null,"version":"v0"}')
    outside = {"OAUTH_CLIENT_SECRET": "old", "PATH": "/bin"}
    assert sign_in_environment(outside, on)["OAUTH_CLIENT_SECRET"] == "s"
    assert "OAUTH_CODE_CHALLENGE_METHOD" not in sign_in_environment(outside, on)
    assert sign_in_environment(outside, public)["OAUTH_CODE_CHALLENGE_METHOD"] == "S256"
    assert "OAUTH_CLIENT_SECRET" not in sign_in_environment(outside, public)
    assert sign_in_environment(outside, off) == {"PATH": "/bin", "ENABLE_OAUTH_SIGNUP": "false"}
    assert sign_in_environment(outside, None) == {"PATH": "/bin", "ENABLE_OAUTH_SIGNUP": "false"}
    assert changed(on, dict(on, version="v2")) and not changed(on, on)
    assert changed(None, on) and not changed(None, off)
    for bad in (b"[]", b'{"configured":true,"version":"v"}', b'{"configured":"yes","version":"v"}', b"not json"):
        try:
            parse(bad)
            raise AssertionError(bad)
        except ValueError:
            pass
    assert exit_code(-15) == 143 and exit_code(3) == 3
    log("self-test passed")


def main(argv):
    if argv[:1] == ["--self-test"]:
        self_test()
        return 0
    environment = dict(os.environ)
    if argv[:1] == ["--secrets-dir"]:
        read_secrets(environment, argv[1])
        argv = argv[2:]
    command = argv[1:] if argv[:1] == ["--"] else ["bash", "start.sh"]
    key = environment.get("OPENAI_API_KEYS", "")
    context = ssl.create_default_context(cafile=os.environ.get("SSL_CERT_FILE") or None)

    deadline = time.monotonic() + WAIT
    started = fetch(key, context)
    while started is None and time.monotonic() < deadline:
        time.sleep(2)
        started = fetch(key, context)
    if started is None:
        log(f"rag-api did not answer within {WAIT} s: starting without OIDC sign-in; nobody can sign in until it answers")
    elif started["configured"]:
        log("starting with OIDC sign-in from rag-api's sign-in settings")
    else:
        log("no sign-in settings yet (rag-admin's setup wizard, or the deployment's UME_RAG_OPENWEBUI_OIDC_*): starting without OIDC sign-in; nobody can sign in")

    child = subprocess.Popen(command, env=sign_in_environment(environment, started))

    def forward(signum, _frame):
        child.send_signal(signum)

    signal.signal(signal.SIGTERM, forward)
    signal.signal(signal.SIGINT, forward)
    while True:
        try:
            return exit_code(child.wait(timeout=POLL))
        except subprocess.TimeoutExpired:
            pass
        answer = fetch(key, context)
        if answer is None:
            continue
        if changed(started, answer):
            log("sign-in settings changed; restarting")
            child.terminate()
            try:
                child.wait(timeout=60)
            except subprocess.TimeoutExpired:
                child.kill()
                child.wait()
            return CHANGED_EXIT
        started = answer if started is None else started


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
