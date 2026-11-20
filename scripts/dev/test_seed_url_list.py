"""Tests for seed_url_list.py that need no database: the connection-string parsing, the localhost-only guard (a
non-local database is refused before psql runs), the URL checks, the psql input (with the source's piiMode) and the
--api-key handling (key format, hash, pepper from the environment only, the key printed once). The SQL itself, and
kalla-api accepting the key, are tested against TestPostgres by tests/Kalla.Worker.Tests/Engine/DevSeedScriptTests.cs.
Standard library only:

    python3 -I -m unittest discover -s scripts/dev -v
"""

import io
import os
import re
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import seed_url_list

PASSWORD = "dev-only-pw"  # noqa: S105 - a test value
LOCAL = f"Host=localhost;Port=54321;Username=postgres;Password={PASSWORD};Database=ragdb"
PEPPER = "dev-only-pepper-0123456789abcdef-xyz"  # a test value


def run_main(argv: list[str]) -> tuple[int, str]:
    err = io.StringIO()
    with redirect_stdout(io.StringIO()), redirect_stderr(err):
        code = seed_url_list.main(argv)
    return code, err.getvalue()


class ConnectionTests(unittest.TestCase):
    def test_npgsql_form(self) -> None:
        parts = seed_url_list.parse_connection(LOCAL)
        self.assertEqual(
            {"host": "localhost", "port": "54321", "user": "postgres", "password": PASSWORD, "database": "ragdb"}, parts
        )

    def test_uri_form(self) -> None:
        parts = seed_url_list.parse_connection("postgresql://postgres:p%3Bw@127.0.0.1:5432/ragdb")
        self.assertEqual(
            ("127.0.0.1", "5432", "p;w", "ragdb"), (parts["host"], parts["port"], parts["password"], parts["database"])
        )

    def test_local_hosts_pass(self) -> None:
        for host in ("localhost", "127.0.0.1", "::1", "LOCALHOST", "localhost,127.0.0.1", "[::1]:5432"):
            with self.subTest(host=host):
                seed_url_list.require_local({"host": host})

    def test_non_local_hosts_are_refused(self) -> None:
        for host in (
            "db.example.org",
            "10.0.0.5",
            "localhost,db.example.org",
            "localhost.example.org",
            "/var/run/postgresql",
        ):
            with self.subTest(host=host), self.assertRaises(seed_url_list.SeedError):
                seed_url_list.require_local({"host": host})

    def test_missing_host_is_refused(self) -> None:
        with self.assertRaises(seed_url_list.SeedError):
            seed_url_list.require_local(seed_url_list.parse_connection("Port=5432;Database=ragdb"))


class MainTests(unittest.TestCase):
    def test_a_non_local_database_is_refused_before_psql_runs(self) -> None:
        with mock.patch("subprocess.run") as run:
            code, err = run_main(
                ["--connection", "Host=rag-db.example.org;Database=ragdb", "--profile", "p", "https://example.org/a"]
            )
        self.assertEqual(2, code)
        self.assertIn("refusing to seed rag-db.example.org", err)
        run.assert_not_called()

    def test_the_environment_variable_is_checked_too(self) -> None:
        with (
            mock.patch.dict("os.environ", {"KALLA_DEV_DB": "Host=10.1.2.3;Database=ragdb"}),
            mock.patch("subprocess.run") as run,
        ):
            code, err = run_main(["--profile", "p", "https://example.org/a"])
        self.assertEqual(2, code)
        self.assertIn("refusing to seed 10.1.2.3", err)
        run.assert_not_called()

    def test_bad_urls_are_refused(self) -> None:
        for url in ("ftp://example.org/a", "example.org/a", "https://" + "a" * 2050):
            with self.subTest(url=url[:30]), mock.patch("subprocess.run") as run:
                code, _ = run_main(["--connection", LOCAL, "--profile", "p", url])
                self.assertEqual(2, code)
                run.assert_not_called()

    def test_psql_gets_the_settings_then_the_sql_file_and_the_password_in_the_environment(self) -> None:
        output = 'set_config\n"[""https://example.org/a""]"\nKey,Value\nsource,abc (created)\nsource_id,abc\n'
        completed = mock.Mock(returncode=0, stdout=output, stderr="")
        out = io.StringIO()
        with mock.patch("subprocess.run", return_value=completed) as run, redirect_stdout(out):
            code = seed_url_list.main(["--connection", LOCAL, "--profile", "pilot-embedding", "https://example.org/a"])
        self.assertEqual(0, code)
        command, kwargs = run.call_args.args[0], run.call_args.kwargs
        self.assertEqual("psql", command[0])
        self.assertNotIn(PASSWORD, " ".join(command))
        self.assertEqual(PASSWORD, kwargs["env"]["PGPASSWORD"])
        self.assertEqual("54321", kwargs["env"]["PGPORT"])
        stdin = kwargs["input"]
        self.assertIn("SELECT set_config('ume_seed.urls', '[\"https://example.org/a\"]', false);", stdin)
        self.assertIn("SELECT set_config('ume_seed.enqueue', 'true', false);", stdin)
        self.assertIn("SELECT set_config('ume_seed.pii_mode', 'report', false);", stdin)
        self.assertTrue(stdin.endswith(seed_url_list.SQL_FILE.read_text(encoding="utf-8")))
        self.assertIn("  source: abc (created)", out.getvalue())
        self.assertNotIn("source_id", out.getvalue())

    def test_pii_mode_hold_is_passed_and_another_value_is_refused_before_psql_runs(self) -> None:
        completed = mock.Mock(returncode=0, stdout="Key,Value\nsource,abc (created)\n", stderr="")
        with mock.patch("subprocess.run", return_value=completed) as run, redirect_stdout(io.StringIO()):
            code = seed_url_list.main(
                ["--connection", LOCAL, "--profile", "p", "--pii-mode", "hold", "https://example.org/a"]
            )
        self.assertEqual(0, code)
        self.assertIn("SELECT set_config('ume_seed.pii_mode', 'hold', false);", run.call_args.kwargs["input"])

        with (
            mock.patch("subprocess.run") as run,
            redirect_stderr(io.StringIO()) as err,
            self.assertRaises(SystemExit) as exit_,
        ):
            seed_url_list.main(["--connection", LOCAL, "--profile", "p", "--pii-mode", "off", "https://example.org/a"])
        self.assertEqual(2, exit_.exception.code)
        self.assertIn("invalid choice: 'off'", err.getvalue())
        run.assert_not_called()

    def test_socket_mode_runs_psql_in_the_container_as_postgres_without_a_password(self) -> None:
        completed = mock.Mock(returncode=0, stdout="Key,Value\nsource,abc (created)\n", stderr="")
        with (
            mock.patch.dict(os.environ, {"PGPASSWORD": "leftover", "KALLA_DEV_DB": ""}),
            mock.patch("subprocess.run", return_value=completed) as run,
            redirect_stdout(io.StringIO()),
        ):
            code = seed_url_list.main(
                ["--docker", "kalla-postgres-1", "--socket", "--profile", "pilot-embedding", "https://example.org/a"]
            )
        self.assertEqual(0, code)
        command, env = run.call_args.args[0], run.call_args.kwargs["env"]
        self.assertEqual(["docker", "exec", "-i", "-u", "postgres", "kalla-postgres-1", "psql"], command[:7])
        self.assertEqual(["-U", "postgres", "-d", "ragdb"], command[-4:])
        self.assertNotIn("-h", command)
        self.assertNotIn("PGPASSWORD", env)

    def test_socket_mode_needs_docker(self) -> None:
        with mock.patch("subprocess.run") as run:
            code, err = run_main(["--socket", "--profile", "p", "https://example.org/a"])
        self.assertEqual(2, code)
        self.assertIn("--socket needs --docker", err)
        run.assert_not_called()

    def test_quotes_in_values_are_escaped(self) -> None:
        self.assertEqual("'O''Brien'", seed_url_list.sql_literal("O'Brien"))


class ApiKeyTests(unittest.TestCase):
    ARGS = ("--connection", LOCAL, "--profile", "pilot-embedding", "--api-key", "https://example.org/a")

    def run_seed(self, sql_output: str, env: dict[str, str]) -> tuple[int, str, str, mock.Mock]:
        completed = mock.Mock(returncode=0, stdout=sql_output, stderr="")
        out, err = io.StringIO(), io.StringIO()
        with (
            mock.patch.dict(os.environ, env),
            mock.patch("subprocess.run", return_value=completed) as run,
            redirect_stdout(out),
            redirect_stderr(err),
        ):
            code = seed_url_list.main(list(self.ARGS))
        return code, out.getvalue(), err.getvalue(), run

    def test_the_key_has_the_key_hashers_format(self) -> None:
        key = seed_url_list.generate_key()
        self.assertRegex(key, r"^kalla-sk-[0-9A-Za-z]{43}$")
        self.assertNotEqual(key, seed_url_list.generate_key())

    def test_the_hash_matches_the_key_hashers_test_vector(self) -> None:
        # The same vector is asserted against KeyHasher.Hash in DevSeedScriptTests.
        key = "kalla-sk-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefg"
        self.assertEqual(
            "f2edf7e535b369dedbb773f85b74b5f2b2ef36cf126281929f0c75d2ca0624bf",
            seed_url_list.key_hash("dev-seed-vector-pepper-0123456789abcdef", key),
        )

    def test_a_new_key_is_printed_once_and_only_its_hash_reaches_psql(self) -> None:
        output = "Key,Value\nclient,c1 (created)\napi_key,k1 dev seed: pilot-public (created)\napi_key_state,created\n"
        code, out, _, run = self.run_seed(output, {"KALLA_DEV_KEY_PEPPER": PEPPER})
        self.assertEqual(0, code)
        keys = re.findall(r"kalla-sk-[0-9A-Za-z]{43}", out)
        self.assertEqual(1, len(keys))
        key = keys[0]
        stdin, env, command = run.call_args.kwargs["input"], run.call_args.kwargs["env"], run.call_args.args[0]
        self.assertNotIn(key, stdin)
        self.assertNotIn(PEPPER, stdin)
        self.assertIn(f"SELECT set_config('ume_seed.key_hash', '{seed_url_list.key_hash(PEPPER, key)}', false);", stdin)
        self.assertIn(f"SELECT set_config('ume_seed.key_prefix', '{key[:15]}', false);", stdin)
        self.assertNotIn("KALLA_DEV_KEY_PEPPER", env)
        self.assertNotIn(PEPPER, " ".join(command))
        self.assertNotIn("api_key_state", out)

    def test_an_existing_key_prints_nothing_secret(self) -> None:
        output = "Key,Value\napi_key,k1 dev seed: pilot-public (existing; not printed again)\napi_key_state,existing\n"
        code, out, _, _ = self.run_seed(output, {"KALLA_DEV_KEY_PEPPER": PEPPER})
        self.assertEqual(0, code)
        self.assertNotRegex(out, r"kalla-sk-[0-9A-Za-z]{43}")
        self.assertIn("(existing; not printed again)", out)

    def test_the_pepper_must_be_set_and_long_enough(self) -> None:
        for pepper, message in (("", "needs the AppHost's key pepper"), ("short", "shorter than 32 bytes")):
            with self.subTest(pepper=pepper):
                code, out, err, run = self.run_seed("", {"KALLA_DEV_KEY_PEPPER": pepper})
                self.assertEqual(2, code)
                self.assertIn(message, err)
                self.assertEqual("", out)
                run.assert_not_called()

    def test_without_the_option_no_key_is_made(self) -> None:
        completed = mock.Mock(returncode=0, stdout="Key,Value\nsource,abc (created)\n", stderr="")
        out = io.StringIO()
        with mock.patch("subprocess.run", return_value=completed) as run, redirect_stdout(out):
            code = seed_url_list.main(["--connection", LOCAL, "--profile", "p", "https://example.org/a"])
        self.assertEqual(0, code)
        self.assertIn("SELECT set_config('ume_seed.key_hash', '', false);", run.call_args.kwargs["input"])
        self.assertNotIn("kalla-sk-", out.getvalue())


if __name__ == "__main__":
    unittest.main()
