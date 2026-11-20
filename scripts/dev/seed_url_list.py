"""Dev seed: a public URL-list source in the local AppHost database (P1a item 18, milestone 1; docs/p1a-local-run.md).
Standard library only; runs seed-url-list.sql (next to this file) with psql. Until the admin's set-up endpoints (J7)
exist, this is how a local run gets a source.

Usage:
  python3 -I scripts/dev/seed_url_list.py --profile pilot-embedding URL [URL ...]
  python3 -I scripts/dev/seed_url_list.py --profile dev-embedding --urls-file urls.txt --no-sync

The connection string is the AppHost's ragdb connection string from the Aspire dashboard (Npgsql form
"Host=localhost;Port=...;Username=postgres;Password=...;Database=ragdb", or a postgresql:// URI), taken from
--connection or the KALLA_DEV_DB environment variable. The script refuses any host other than localhost, 127.0.0.1
or ::1: it is for a developer's own database only. The password reaches psql through PGPASSWORD, never the command line.

It finds or creates the department (by name, or --department-id for a synced one), the collection (by slug: public,
published, on the embedding profile) and the URL-list source (by name in the collection; a re-run replaces its URL
list), on the platform url-list pipeline the migrator publishes, then enqueues a manual sync as the admin's "sync now"
does (--no-sync leaves it to the scheduler's url-list schedule). It prints what it found, created and enqueued. The
source gets piiMode report, as a source on the municipality's own published site does (Decisions table, 2026-11-01);
--pii-mode hold keeps personal-data flags holding the chunk set.

--api-key also creates, until the admin's key endpoints (J7) exist, what a /v1/retrieve call on kalla-api needs: the
public level's data-protection row (when missing), an internal client with ceiling public (by --client-name) and a
secret key of that client scoped to the collection. The key is generated here (secrets, the KeyHasher format
kalla-sk- + 43 base62) and only its HMAC-SHA256 with the AppHost's key pepper reaches the database; the pepper comes
from the KALLA_DEV_KEY_PEPPER environment variable (the key-pepper parameter in the Aspire dashboard), never from the
command line. The plaintext key is printed once, to stdout, when the key is created; a re-run that finds a usable key
of the client for the collection says so and prints nothing secret. Nothing writes the key to a file.

psql comes from PATH, or from the AppHost's Postgres container with --docker CONTAINER (docker exec; the container's own
port 5432). On the production Compose stack (a pilot or test host, docs/production-setup.md) the superuser may only
connect over the Unix socket: --docker kalla-postgres-1 --socket runs psql there as the container's postgres user
(peer authentication, no password, no connection string). Exit 0 when seeded, 1 when psql or the SQL failed, 2 on bad
arguments or a non-local database.
"""

import argparse
import csv
import hashlib
import hmac
import io
import json
import os
import secrets
import subprocess
import sys
import urllib.parse
from pathlib import Path

SQL_FILE = Path(__file__).resolve().parent / "seed-url-list.sql"
ENV_CONNECTION = "KALLA_DEV_DB"
LOCAL_HOSTS = frozenset({"localhost", "127.0.0.1", "::1", "[::1]"})
MAX_URLS = 5000  # UrlListSettings.Urls
MAX_URL_LENGTH = 2048
ENV_PEPPER = "KALLA_DEV_KEY_PEPPER"
KEY_PREFIX = "kalla-sk-"  # KeyHasher.SecretPrefix
KEY_ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"  # KeyHasher's base62
KEY_RANDOM_CHARS = 43
KEY_DISPLAY_PREFIX = 13  # "kalla-sk-" + 4 characters
MIN_PEPPER_BYTES = 32  # KeyHasher and Security:KeyPepper
NPGSQL_KEYS = {
    "host": "host",
    "server": "host",
    "port": "port",
    "username": "user",
    "user id": "user",
    "userid": "user",
    "user": "user",
    "password": "password",
    "database": "database",
    "db": "database",
}


class SeedError(Exception):
    """A bad argument or a non-local database: exit 2."""


def parse_connection(value: str) -> dict[str, str]:
    """The host, port, user, password and database of an Npgsql connection string or a postgres URI."""
    value = value.strip()
    if value.startswith(("postgres://", "postgresql://")):
        uri = urllib.parse.urlsplit(value)
        parts = {
            "host": uri.hostname or "",
            "port": str(uri.port or ""),
            "user": urllib.parse.unquote(uri.username or ""),
            "password": urllib.parse.unquote(uri.password or ""),
            "database": uri.path.lstrip("/"),
        }
    else:
        parts = {}
        for item in value.split(";"):
            if not item.strip():
                continue
            key, sep, raw = item.partition("=")
            if not sep:
                raise SeedError(f"connection string: '{item.strip()}' is not key=value")
            name = NPGSQL_KEYS.get(key.strip().lower())
            if name is not None:
                parts[name] = raw.strip().strip("'\"")
    return {k: v for k, v in parts.items() if v}


def require_local(parts: dict[str, str]) -> None:
    """Refuses a connection whose host (every host of a multi-host string) is not this machine."""
    hosts = [h.strip().lower() for h in parts.get("host", "").split(",") if h.strip()]
    if not hosts:
        raise SeedError("connection string: no Host; this script runs only against localhost")
    remote = [h for h in hosts if h.rsplit(":", 1)[0] not in LOCAL_HOSTS and h not in LOCAL_HOSTS]
    if remote:
        raise SeedError(
            f"refusing to seed {', '.join(remote)}: this dev script runs only against localhost or 127.0.0.1"
        )


def read_urls(args: argparse.Namespace) -> list[str]:
    urls = list(args.urls)
    if args.urls_file:
        text = Path(args.urls_file).read_text(encoding="utf-8")
        urls += [line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")]
    urls = list(dict.fromkeys(urls))
    if not urls:
        raise SeedError("no URLs: give them as arguments or with --urls-file")
    if len(urls) > MAX_URLS:
        raise SeedError(f"{len(urls)} URLs: a URL-list source takes at most {MAX_URLS}")
    for url in urls:
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme not in ("http", "https") or not parsed.hostname or len(url) > MAX_URL_LENGTH:
            raise SeedError(f"not an absolute http(s) URL of at most {MAX_URL_LENGTH} characters: {url}")
    return urls


def generate_key() -> str:
    """A secret key as kalla-api's KeyHasher.Generate makes one: the prefix and 43 random base62 characters."""
    return KEY_PREFIX + "".join(secrets.choice(KEY_ALPHABET) for _ in range(KEY_RANDOM_CHARS))


def key_hash(pepper: str, key: str) -> str:
    """KeyHasher.Hash: hex HMAC-SHA256 of the key's UTF-8 bytes under the pepper's UTF-8 bytes."""
    return hmac.new(pepper.encode("utf-8"), key.encode("utf-8"), hashlib.sha256).hexdigest()


def read_pepper() -> str:
    pepper = os.environ.get(ENV_PEPPER, "")
    if not pepper:
        raise SeedError(f"--api-key needs the AppHost's key pepper in {ENV_PEPPER} (the key-pepper parameter)")
    if len(pepper.encode("utf-8")) < MIN_PEPPER_BYTES:
        raise SeedError(f"{ENV_PEPPER} is shorter than {MIN_PEPPER_BYTES} bytes; copy the whole key-pepper value")
    return pepper


def sql_literal(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def script(args: argparse.Namespace, urls: list[str], key: str | None = None, pepper: str | None = None) -> str:
    """The parameters as session settings, then seed-url-list.sql: what psql reads on stdin. Of a key, only its hash
    and display prefix."""
    settings = {
        "kalla_seed.urls": json.dumps(urls),
        "kalla_seed.profile": args.profile,
        "kalla_seed.department_id": args.department_id or "",
        "kalla_seed.department_name": args.department_name,
        "kalla_seed.collection_slug": args.collection_slug,
        "kalla_seed.collection_name": args.collection_name,
        "kalla_seed.source_name": args.source_name,
        "kalla_seed.pii_mode": args.pii_mode,
        "kalla_seed.enqueue": "false" if args.no_sync else "true",
        "kalla_seed.client_name": args.client_name,
        "kalla_seed.key_hash": key_hash(pepper, key) if key and pepper else "",
        "kalla_seed.key_prefix": key[:KEY_DISPLAY_PREFIX] if key else "",
    }
    lines = [f"SELECT set_config({sql_literal(k)}, {sql_literal(v)}, false);" for k, v in settings.items()]
    return "\n".join(lines) + "\n" + SQL_FILE.read_text(encoding="utf-8")


def psql_command(parts: dict[str, str], docker: str | None, socket: bool = False) -> tuple[list[str], dict[str, str]]:
    psql = ["psql", "-X", "-q", "--csv", "-v", "ON_ERROR_STOP=1", "-f", "-"]
    if socket:
        # The container's Unix socket as its own postgres user (peer): no password in this process or its environment.
        if docker is None:
            raise SeedError("--socket needs --docker CONTAINER")
        env = {k: v for k, v in os.environ.items() if not k.startswith("PG")}
        return [
            "docker",
            "exec",
            "-i",
            "-u",
            "postgres",
            docker,
            *psql,
            "-U",
            "postgres",
            "-d",
            parts.get("database", "ragdb"),
        ], env
    env = dict(os.environ)
    env.update(
        {
            "PGHOST": parts.get("host", "localhost").split(",")[0],
            "PGPORT": parts.get("port", "5432"),
            "PGUSER": parts.get("user", "postgres"),
            "PGPASSWORD": parts.get("password", ""),
            "PGDATABASE": parts.get("database", "ragdb"),
        }
    )
    if docker is None:
        return psql, env
    passed = [a for name in ("PGUSER", "PGPASSWORD", "PGDATABASE") for a in ("-e", name)]
    return ["docker", "exec", "-i", *passed, docker, *psql, "-h", "127.0.0.1", "-p", "5432"], env


def rows(output: str) -> list[tuple[str, str]]:
    """The SQL's result rows (psql --csv), without the header."""
    return [(row[0], row[1]) for row in csv.reader(io.StringIO(output)) if len(row) == 2 and row[0] != "Key"]


def report(output: str) -> list[str]:
    """The result rows as "key: value" lines; the bare ids and states (keys ending in _id or _state) are for tests."""
    return [f"{key}: {value}" for key, value in rows(output) if not key.endswith(("_id", "_state"))]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n\n")[0], formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument("urls", nargs="*", metavar="URL", help="absolute http(s) URLs of the source")
    parser.add_argument("--urls-file", help="a file with one URL per line (# comments)")
    parser.add_argument("--connection", help=f"the ragdb connection string (default: ${ENV_CONNECTION})")
    parser.add_argument(
        "--profile", required=True, help="embedding profile name: pilot-embedding (EmbeddingProfile:*) or dev-embedding"
    )
    parser.add_argument("--department-id", help="an existing (synced) department; default: find or create by name")
    parser.add_argument("--department-name", default="Pilot (dev seed)")
    parser.add_argument("--collection-slug", default="pilot-public")
    parser.add_argument("--collection-name", default="Pilot: umea.se (public)")
    parser.add_argument("--source-name", default="Pilot URL list")
    parser.add_argument(
        "--pii-mode",
        choices=("report", "hold"),
        default="report",
        help="the source's piiMode: report (default; the municipality's own site) or hold",
    )
    parser.add_argument(
        "--no-sync", action="store_true", help="enqueue no sync; the scheduler runs the source on its schedule"
    )
    parser.add_argument(
        "--api-key",
        action="store_true",
        help=f"also an internal client and a key for /v1/retrieve on the collection (pepper from ${ENV_PEPPER})",
    )
    parser.add_argument("--client-name", default="Pilot retrieval (dev seed)", help="the internal client of --api-key")
    parser.add_argument("--docker", metavar="CONTAINER", help="run psql in this container (the AppHost's Postgres)")
    parser.add_argument(
        "--socket",
        action="store_true",
        help="with --docker: connect over the container's Unix socket as postgres (peer); no connection string",
    )
    args = parser.parse_args(argv)
    try:
        if args.socket:
            if not args.docker:
                raise SeedError("--socket needs --docker CONTAINER")
            parts = {"host": f"{args.docker} (socket)", "database": "ragdb", "user": "postgres"}
        else:
            connection = args.connection or os.environ.get(ENV_CONNECTION)
            if not connection:
                raise SeedError(f"no connection string: pass --connection or set {ENV_CONNECTION}")
            parts = parse_connection(connection)
            require_local(parts)
        urls = read_urls(args)
        pepper = read_pepper() if args.api_key else None
    except (SeedError, OSError) as error:
        print(f"seed-url-list: {error}", file=sys.stderr)
        return 2

    key = generate_key() if args.api_key else None
    command, env = psql_command(parts, args.docker, args.socket)
    env.pop(ENV_PEPPER, None)
    try:
        result = subprocess.run(  # noqa: S603 - fixed argv; the SQL is our file, the values are quoted literals
            command, input=script(args, urls, key, pepper), capture_output=True, text=True, env=env, check=False
        )
    except FileNotFoundError:
        print(f"seed-url-list: {command[0]} not found; install psql or use --docker CONTAINER", file=sys.stderr)
        return 1
    if result.returncode != 0:
        print(result.stderr.strip() or f"seed-url-list: psql exited with {result.returncode}", file=sys.stderr)
        return 1

    print(f"Seeded {parts.get('database', 'ragdb')} on {parts.get('host')}:")
    for line in report(result.stdout):
        print(f"  {line}")
    if key is not None and dict(rows(result.stdout)).get("api_key_state") == "created":
        print("API key for /v1/retrieve, shown only now (only its hash is stored; copy it, it is written nowhere):")
        print(key)
    return 0


if __name__ == "__main__":
    sys.exit(main())
