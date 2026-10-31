"""Tests for seed_url_list.py that need no database: the connection-string parsing, the localhost-only guard (a
non-local database is refused before psql runs), the URL checks and the psql input. The SQL itself is tested against
TestPostgres by tests/Ume.Rag.Worker.Tests/Engine/DevSeedScriptTests.cs. Standard library only:

    python3 -I -m unittest discover -s scripts/dev -v
"""

import io
import sys
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import seed_url_list

PASSWORD = "dev-only-pw"  # noqa: S105 - a test value
LOCAL = f"Host=localhost;Port=54321;Username=postgres;Password={PASSWORD};Database=ragdb"


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
            mock.patch.dict("os.environ", {"UME_RAG_DEV_DB": "Host=10.1.2.3;Database=ragdb"}),
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
        self.assertTrue(stdin.endswith(seed_url_list.SQL_FILE.read_text(encoding="utf-8")))
        self.assertIn("  source: abc (created)", out.getvalue())
        self.assertNotIn("source_id", out.getvalue())

    def test_quotes_in_values_are_escaped(self) -> None:
        self.assertEqual("'O''Brien'", seed_url_list.sql_literal("O'Brien"))


if __name__ == "__main__":
    unittest.main()
