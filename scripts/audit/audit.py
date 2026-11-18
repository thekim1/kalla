"""Dependency audit checks shared by verify.yml (each pull request) and audit.yml (daily against main), so the two
cannot drift (plans/platform/repo-and-deployment.md §8.2, §8.4). Standard library only; it reads a report the
package manager wrote and never runs it.

Usage:
  dotnet list UmeRag.slnx package --vulnerable --include-transitive --format json > dotnet.json
  python3 -I scripts/audit/audit.py dotnet dotnet.json [--summary FILE]

  npm audit --json > npm.json || true          # in src/admin-ui, after npm ci
  python3 -I scripts/audit/audit.py npm npm.json [--exceptions FILE] [--production-exceptions FILE]
      [--today YYYY-MM-DD] [--summary FILE]

  npm audit --omit=dev --json > prod.json || true
  npx vite build --sourcemap hidden --outDir modules --emptyOutDir   # the bundle's module list, in its source maps
  python3 -I scripts/audit/audit.py npm-production prod.json --modules modules [--exceptions FILE]
      [--today YYYY-MM-DD] [--summary FILE]

  grype registry:<image> -o json --file <name>.json -q    # one report per image
  python3 -I scripts/audit/audit.py grype <name>=<name>.json ... [--exceptions FILE] [--today YYYY-MM-DD]
      [--title TEXT] [--summary FILE]

dotnet  fails on any top-level or transitive package with a vulnerability, and on an error the report lists.
npm     fails on any advisory without a current exception. An exception is a line "<GHSA id> <last day, YYYY-MM-DD>
        <reason>" in the exceptions file (default: npm-audit-exceptions.txt next to this script); it stops applying
        after its last day, so the check turns red again unless the fix has landed. A current production exception
        (below) applies here too.
npm-production
        fails on any advisory against a production dependency, unless a current exception lists it and the package is
        not in the built bundle: a line "<GHSA id> <package> <last day, YYYY-MM-DD> <reason>" in the exceptions file
        (default: npm-production-exceptions.txt next to this script). The bundle's packages are read from the source
        maps' sources below --modules; without source maps the check fails. An expired exception, and one that
        matched no advisory, is printed as a workflow warning.
grype   fails on a High or Critical finding with a fix in any image, unless a current exception lists it: a line
        "<vulnerability id> <image> <package> <last day, YYYY-MM-DD> <reason>" in the exceptions file (default:
        image-scan-exceptions.txt next to this script). An expired exception, and one that matched no finding, is
        printed as a workflow warning.

Exit 0 when clean, 1 with a message on stderr otherwise. --summary appends a short Markdown summary to FILE
(the workflow's $GITHUB_STEP_SUMMARY).
"""

from __future__ import annotations

import argparse
import datetime
import json
import sys
from pathlib import Path

DEFAULT_EXCEPTIONS = Path(__file__).resolve().parent / "npm-audit-exceptions.txt"
DEFAULT_IMAGE_EXCEPTIONS = Path(__file__).resolve().parent / "image-scan-exceptions.txt"
DEFAULT_PRODUCTION_EXCEPTIONS = Path(__file__).resolve().parent / "npm-production-exceptions.txt"
SERIOUS = ("Critical", "High")


def dotnet(report: dict) -> tuple[list[str], list[str]]:
    """Returns (findings, errors): one line per vulnerable package and advisory, one per error in the report."""
    findings = []
    for project in report.get("projects", []):
        name = Path(project.get("path", "?")).name
        for framework in project.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    where = "top-level" if kind == "topLevelPackages" else "transitive"
                    for vulnerability in package.get("vulnerabilities") or []:
                        findings.append(
                            f"{name} ({framework.get('framework', '?')}): {package.get('id')} "
                            f"{package.get('resolvedVersion')} ({where}): "
                            f"{vulnerability.get('severity')} {vulnerability.get('advisoryurl')}"
                        )
    errors = [p.get("text", str(p)) for p in report.get("problems", []) if p.get("level") == "error"]
    return findings, errors


def read_exceptions(path: Path) -> dict[str, str]:
    allowed = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split(maxsplit=2)
        try:
            if len(parts) < 3 or not parts[0].startswith("GHSA-"):
                raise ValueError
            datetime.date.fromisoformat(parts[1])
        except ValueError:
            sys.exit(f"{path}:{number}: expected '<GHSA id> <YYYY-MM-DD> <reason>', got: {line}")
        allowed[parts[0]] = parts[1]
    return allowed


def npm(report: dict, allowed: dict[str, str], today: str) -> list[tuple[str, str, bool]]:
    """Returns (advisory, packages, fails) per advisory, sorted by advisory id."""
    found: dict[str, set[str]] = {}
    for vuln in report["vulnerabilities"].values():
        for via in vuln["via"]:
            if isinstance(via, dict):
                found.setdefault(via["url"].rsplit("/", 1)[-1], set()).add(f"{via['name']} ({via['severity']})")
    out = []
    for ghsa, where in sorted(found.items()):
        until = allowed.get(ghsa)
        out.append((ghsa, ", ".join(sorted(where)), not (until and today <= until)))
    return out


def read_image_exceptions(path: Path) -> dict[tuple[str, str, str], str]:
    """Returns {(vulnerability id, image, package): last day}."""
    allowed = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split(maxsplit=4)
        try:
            if len(parts) < 5:
                raise ValueError
            datetime.date.fromisoformat(parts[3])
        except ValueError:
            sys.exit(f"{path}:{number}: expected '<id> <image> <package> <YYYY-MM-DD> <reason>', got: {line}")
        allowed[(parts[0], parts[1], parts[2])] = parts[3]
    return allowed


def grype(image: str, report: dict, allowed: dict[tuple[str, str, str], str], today: str) -> dict:
    """Counts one image's matches; "fails" and "excepted" are the High and Critical findings with a fix."""
    counts = {"Critical": 0, "High": 0, "lower": 0}
    fails: set[str] = set()
    excepted: set[tuple[str, str, str]] = set()
    for match in report.get("matches", []):
        vulnerability, artifact = match["vulnerability"], match["artifact"]
        severity = vulnerability.get("severity")
        counts[severity if severity in SERIOUS else "lower"] += 1
        if severity not in SERIOUS or vulnerability.get("fix", {}).get("state") != "fixed":
            continue
        key = (vulnerability["id"], image, artifact["name"])
        until = allowed.get(key)
        if until and today <= until:
            excepted.add(key)
        else:
            fixed = ", ".join(vulnerability["fix"].get("versions", []))
            fails.add(f"{vulnerability['id']} {severity} {artifact['name']} {artifact.get('version')} -> {fixed}")
    return {**counts, "fails": sorted(fails), "excepted": excepted}


def grype_main(args: argparse.Namespace) -> int:
    allowed = read_image_exceptions(args.exceptions)
    for (vid, image, package), until in sorted(allowed.items()):
        if until < args.today:
            print(f"::warning::image scan exception {vid} ({image}, {package}) expired on {until}, no longer applies")
    rows = [
        "| Image | Critical | High | with a fix, failing | with a fix, excepted | Medium and lower |",
        "|---|---|---|---|---|---|",
    ]
    details, used, failing_images = [], set(), []
    for pair in args.reports:
        image, _, path = pair.partition("=")
        result = grype(image, json.loads(Path(path).read_text(encoding="utf-8")), allowed, args.today)
        used |= result["excepted"]
        failing, excepted = len(result["fails"]), len(result["excepted"])
        critical, high = result["Critical"], result["High"]
        rows.append(f"| {image} | {critical} | {high} | {failing} | {excepted} | {result['lower']} |")
        print(f"{image}: {critical} critical, {high} high ({failing} failing, {excepted} excepted)")
        for line in result["fails"]:
            print(f"  {line}")
        if result["fails"]:
            failing_images.append(image)
            details += [f"<details><summary>{image}: High and Critical with a fix</summary>", "", "```"]
            details += result["fails"] + ["```", "</details>", ""]
    scanned = {pair.partition("=")[0] for pair in args.reports}
    for key, until in sorted(allowed.items()):
        if until >= args.today and key[1] in scanned and key not in used:
            print(f"::warning::image scan exception {key[0]} ({key[1]}, {key[2]}) matched no finding: remove it")
    append(args.summary, [f"### Image scan{args.title}", "", *rows, "", *details])
    if failing_images:
        images = ", ".join(failing_images)
        sys.exit(f"High or Critical vulnerabilities with a fix in {images}: bump the base image or the package")
    return 0


def read_production_exceptions(path: Path) -> dict[tuple[str, str], str]:
    """Returns {(GHSA id, package): last day}."""
    allowed = {}
    for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split(maxsplit=3)
        try:
            if len(parts) < 4 or not parts[0].startswith("GHSA-"):
                raise ValueError
            datetime.date.fromisoformat(parts[2])
        except ValueError:
            sys.exit(f"{path}:{number}: expected '<GHSA id> <package> <YYYY-MM-DD> <reason>', got: {line}")
        allowed[(parts[0], parts[1])] = parts[2]
    return allowed


def bundled_packages(modules: Path) -> set[str]:
    """The npm packages a build bundled, from its source maps' sources (node_modules/<package>/...)."""
    maps = sorted(modules.rglob("*.map"))
    if not maps:
        sys.exit(f"no source maps below {modules}: build with --sourcemap so the bundle's packages can be read")
    packages = set()
    for path in maps:
        for source in json.loads(path.read_text(encoding="utf-8")).get("sources", []):
            if "node_modules/" in source:
                parts = source.rsplit("node_modules/", 1)[1].split("/")
                packages.add("/".join(parts[:2]) if parts[0].startswith("@") else parts[0])
    return packages


def npm_production_main(args: argparse.Namespace) -> int:
    report = json.loads(args.report.read_text(encoding="utf-8"))
    title = "### npm audit, production dependencies"
    if "vulnerabilities" not in report:
        message = f"npm audit gave no report: {report.get('error', report)}"
        append(args.summary, [title, "", message])
        sys.exit(message)
    allowed = read_production_exceptions(args.exceptions)
    for (ghsa, package), until in sorted(allowed.items()):
        if until < args.today:
            print(f"::warning::npm production exception {ghsa} ({package}) expired on {until}, no longer applies")
    bundled = bundled_packages(args.modules)
    found: set[tuple[str, str, str]] = set()
    for vuln in report["vulnerabilities"].values():
        for via in vuln["via"]:
            if isinstance(via, dict):
                found.add((via["url"].rsplit("/", 1)[-1], via["name"], via["severity"]))
    lines, bad, used = [], [], set()
    for ghsa, package, severity in sorted(found):
        until = allowed.get((ghsa, package))
        if until and args.today <= until and package not in bundled:
            used.add((ghsa, package))
            state = f"excepted until {until}, not in the bundle"
        elif until and args.today <= until:
            state = "FAILS: excepted, but the package is in the bundle"
            bad.append(ghsa)
        else:
            state = "FAILS"
            bad.append(ghsa)
        lines.append(f"{ghsa}: {package} ({severity}): {state}")
        print(lines[-1])
    for key, until in sorted(allowed.items()):
        if until >= args.today and key not in used and key not in {(g, p) for g, p, _ in found}:
            print(f"::warning::npm production exception {key[0]} ({key[1]}) matched no advisory: remove it")
    append(
        args.summary,
        [f"{title} ({len(bundled)} packages in the bundle)", ""] + [f"- {x}" for x in lines or ["No advisories."]],
    )
    if bad:
        sys.exit(f"{len(bad)} production advisory(ies) without a current exception: {', '.join(sorted(set(bad)))}")
    return 0


def iso_date(value: str) -> str:
    return datetime.date.fromisoformat(value).isoformat()


def append(path: str | None, lines: list[str]) -> None:
    if path:
        with open(path, "a", encoding="utf-8") as summary:
            summary.write("\n".join(lines) + "\n\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = parser.add_subparsers(dest="ecosystem", required=True)
    p_dotnet = sub.add_parser("dotnet", help="dotnet list package --vulnerable --include-transitive --format json")
    p_dotnet.add_argument("report", type=Path)
    p_dotnet.add_argument("--summary")
    p_npm = sub.add_parser("npm", help="npm audit --json")
    p_npm.add_argument("report", type=Path)
    p_npm.add_argument("--exceptions", type=Path, default=DEFAULT_EXCEPTIONS)
    p_npm.add_argument("--production-exceptions", type=Path, default=DEFAULT_PRODUCTION_EXCEPTIONS)
    p_npm.add_argument("--today", type=iso_date, default=datetime.date.today().isoformat())
    p_npm.add_argument("--summary")
    p_prod = sub.add_parser("npm-production", help="npm audit --omit=dev --json, with the bundle's source maps")
    p_prod.add_argument("report", type=Path)
    p_prod.add_argument("--modules", type=Path, required=True, help="a build with source maps (vite build --sourcemap)")
    p_prod.add_argument("--exceptions", type=Path, default=DEFAULT_PRODUCTION_EXCEPTIONS)
    p_prod.add_argument("--today", type=iso_date, default=datetime.date.today().isoformat())
    p_prod.add_argument("--summary")
    p_grype = sub.add_parser("grype", help="grype -o json, one report per image")
    p_grype.add_argument("reports", nargs="+", metavar="IMAGE=REPORT")
    p_grype.add_argument("--exceptions", type=Path, default=DEFAULT_IMAGE_EXCEPTIONS)
    p_grype.add_argument("--today", type=iso_date, default=datetime.date.today().isoformat())
    p_grype.add_argument("--title", default="", help="appended to the summary's heading, e.g. ', release 0.7.0'")
    p_grype.add_argument("--summary")
    args = parser.parse_args(argv)
    if args.ecosystem == "grype":
        return grype_main(args)
    if args.ecosystem == "npm-production":
        return npm_production_main(args)
    report = json.loads(args.report.read_text(encoding="utf-8"))

    if args.ecosystem == "dotnet":
        findings, errors = dotnet(report)
        for line in errors + findings:
            print(line)
        audited = f"{len(report.get('projects', []))} projects, top-level and transitive packages"
        append(
            args.summary,
            [f"### .NET packages ({audited})", ""]
            + ([f"- error: {e}" for e in errors] + [f"- {f}" for f in findings] or ["No vulnerable packages."]),
        )
        if errors:
            sys.exit(f"dotnet list package reported {len(errors)} error(s)")
        if findings:
            sys.exit("Vulnerable .NET dependencies detected")
        return 0

    if "vulnerabilities" not in report:
        message = f"npm audit gave no report: {report.get('error', report)}"
        append(args.summary, ["### npm audit, all dependencies", "", message])
        sys.exit(message)
    allowed = read_exceptions(args.exceptions)
    for (ghsa, _), until in read_production_exceptions(args.production_exceptions).items():
        allowed[ghsa] = max(until, allowed.get(ghsa, until))
    advisories = npm(report, allowed, args.today)
    lines = []
    for ghsa, where, fails in advisories:
        state = "FAILS" if fails else "excepted until " + allowed[ghsa]
        lines.append(f"{ghsa}: {where}: {state}")
        print(lines[-1])
    total = report.get("metadata", {}).get("dependencies", {}).get("total", "?")
    append(
        args.summary,
        [f"### npm audit, all dependencies ({total} packages)", ""]
        + ([f"- {line}" for line in lines] or ["No advisories."]),
    )
    bad = [ghsa for ghsa, _, fails in advisories if fails]
    if bad:
        sys.exit(f"{len(bad)} advisory(ies) without a current exception: {', '.join(bad)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
