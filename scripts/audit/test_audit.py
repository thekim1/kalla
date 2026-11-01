"""Tests for audit.py against real reports in tests/fixtures/audit/ (a clean run of this repo and one vulnerable
package each, Newtonsoft.Json 12.0.1 and minimist 1.2.5). Standard library only:

    python3 -I -m unittest discover -s scripts/audit -v
"""

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FIXTURES = ROOT / "tests" / "fixtures" / "audit"
SCRIPT = Path(__file__).resolve().parent / "audit.py"
GHSA = "GHSA-xvch-5gv4-984h"

# The check verify.yml's backend job ran inline before audit.py, kept here to show both agree on the fixtures.
OLD_DOTNET = (
    "import json, os; d=json.load(open(os.path.join(os.environ['RUNNER_TEMP'], 'ume-package-audit.json'))); "
    "p=[p for x in d.get('projects',[]) for f in x.get('frameworks',[]) for k in ('topLevelPackages',"
    "'transitivePackages') for p in f.get(k,[]) if p.get('vulnerabilities')]; "
    "assert not p, 'Vulnerable .NET dependencies detected'"
)


def run(*args: str) -> subprocess.CompletedProcess[str]:
    argv = [sys.executable, "-I", str(SCRIPT), *args]
    return subprocess.run(argv, capture_output=True, text=True, check=False)  # noqa: S603


class Temp(unittest.TestCase):
    def setUp(self) -> None:
        self.dir = Path(self.enterContext(tempfile.TemporaryDirectory()))

    def write(self, name: str, text: str) -> str:
        path = self.dir / name
        path.write_text(text, encoding="utf-8")
        return str(path)


class DotnetTests(Temp):
    def test_clean_report_passes(self) -> None:
        result = run("dotnet", str(FIXTURES / "dotnet-clean.json"))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "")

    def test_vulnerable_top_level_package_fails(self) -> None:
        result = run("dotnet", str(FIXTURES / "dotnet-vulnerable.json"))
        self.assertEqual(result.returncode, 1)
        self.assertIn("Vulnerable .NET dependencies detected", result.stderr)
        self.assertIn(
            "Example.csproj (net10.0): Newtonsoft.Json 12.0.1 (top-level): High "
            "https://github.com/advisories/GHSA-5crp-9r3c-p9vr",
            result.stdout,
        )

    def test_vulnerable_transitive_package_fails(self) -> None:
        report = json.loads((FIXTURES / "dotnet-vulnerable.json").read_text(encoding="utf-8"))
        framework = report["projects"][0]["frameworks"][0]
        framework["transitivePackages"] = framework.pop("topLevelPackages")
        result = run("dotnet", self.write("transitive.json", json.dumps(report)))
        self.assertEqual(result.returncode, 1)
        self.assertIn("Newtonsoft.Json 12.0.1 (transitive)", result.stdout)

    def test_error_in_report_fails(self) -> None:
        report = {"version": 1, "projects": [], "problems": [{"level": "error", "text": "No assets file was found"}]}
        result = run("dotnet", self.write("problems.json", json.dumps(report)))
        self.assertEqual(result.returncode, 1)
        self.assertIn("reported 1 error(s)", result.stderr)

    def test_agrees_with_the_old_inline_check(self) -> None:
        for name, expected in (("dotnet-clean.json", 0), ("dotnet-vulnerable.json", 1)):
            with self.subTest(name):
                self.write("ume-package-audit.json", (FIXTURES / name).read_text(encoding="utf-8"))
                env = {**os.environ, "RUNNER_TEMP": str(self.dir)}
                argv = [sys.executable, "-I", "-c", OLD_DOTNET]
                old = subprocess.run(argv, env=env, capture_output=True, check=False)  # noqa: S603
                self.assertEqual(old.returncode, expected)
                self.assertEqual(run("dotnet", str(FIXTURES / name)).returncode, expected)

    def test_summary(self) -> None:
        summary = self.write("summary.md", "")
        run("dotnet", str(FIXTURES / "dotnet-clean.json"), "--summary", summary)
        run("dotnet", str(FIXTURES / "dotnet-vulnerable.json"), "--summary", summary)
        text = Path(summary).read_text(encoding="utf-8")
        self.assertIn(
            "### .NET packages (21 projects, top-level and transitive packages)\n\nNo vulnerable packages.", text
        )
        self.assertIn("- Example.csproj (net10.0): Newtonsoft.Json 12.0.1 (top-level): High", text)


class NpmTests(Temp):
    def npm(self, report: str, exceptions: str = "", today: str = "2026-11-01", *extra: str):
        exceptions_file = self.write("exceptions.txt", exceptions)
        return run("npm", str(FIXTURES / report), "--exceptions", exceptions_file, "--today", today, *extra)

    def test_clean_report_passes(self) -> None:
        result = self.npm("npm-clean.json")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_advisory_without_exception_fails(self) -> None:
        result = self.npm("npm-vulnerable.json")
        self.assertEqual(result.returncode, 1)
        self.assertIn(f"{GHSA}: minimist (critical): FAILS", result.stdout)
        self.assertIn(f"1 advisory(ies) without a current exception: {GHSA}", result.stderr)

    def test_current_exception_passes_through_its_last_day(self) -> None:
        for today in ("2026-11-01", "2026-11-14"):
            with self.subTest(today):
                result = self.npm("npm-vulnerable.json", f"# note\n{GHSA} 2026-11-14 dev-only\n", today)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(f"{GHSA}: minimist (critical): excepted until 2026-11-14", result.stdout)

    def test_expired_exception_fails(self) -> None:
        result = self.npm("npm-vulnerable.json", f"{GHSA} 2026-10-31 dev-only, fix not 7 days old\n")
        self.assertEqual(result.returncode, 1)
        self.assertIn(f"{GHSA}: minimist (critical): FAILS", result.stdout)
        self.assertIn("1 advisory(ies) without a current exception", result.stderr)

    def test_exception_for_another_advisory_does_not_apply(self) -> None:
        result = self.npm("npm-vulnerable.json", "GHSA-aaaa-bbbb-cccc 2027-01-01 another package\n")
        self.assertEqual(result.returncode, 1)

    def test_malformed_exception_fails(self) -> None:
        for line in (f"{GHSA} 2026-11-14", f"{GHSA} 14/11/2026 reason", "CVE-2021-44906 2026-11-14 reason"):
            with self.subTest(line):
                result = self.npm("npm-clean.json", line + "\n")
                self.assertEqual(result.returncode, 1)
                self.assertIn("expected '<GHSA id> <YYYY-MM-DD> <reason>'", result.stderr)

    def test_error_report_fails(self) -> None:
        path = self.write("error.json", json.dumps({"error": {"code": "ENOLOCK", "summary": "no lockfile"}}))
        result = run("npm", path, "--exceptions", self.write("exceptions.txt", ""))
        self.assertEqual(result.returncode, 1)
        self.assertIn("npm audit gave no report: {'code': 'ENOLOCK'", result.stderr)

    def test_repository_exceptions_file_parses(self) -> None:
        result = run("npm", str(FIXTURES / "npm-clean.json"))
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_summary(self) -> None:
        summary = self.write("summary.md", "")
        self.npm("npm-clean.json", "", "2026-11-01", "--summary", summary)
        self.npm("npm-vulnerable.json", "", "2026-11-01", "--summary", summary)
        text = Path(summary).read_text(encoding="utf-8")
        self.assertIn("### npm audit, all dependencies (317 packages)\n\nNo advisories.", text)
        self.assertIn(f"### npm audit, all dependencies (1 packages)\n\n- {GHSA}: minimist (critical): FAILS", text)


if __name__ == "__main__":
    unittest.main()
