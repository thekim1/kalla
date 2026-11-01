# Dependency audits

`audit.py` checks the reports of `dotnet list package --vulnerable` and `npm audit`, for the `backend` and `admin-ui`
jobs of `.github/workflows/verify.yml` (each pull request that changes code) and for `.github/workflows/audit.yml`
(daily against `main` and from the Actions tab), so a new advisory against an unchanged `main` shows up the next
morning. Both workflows call this one file, so the checks cannot drift. The parser's `pip-audit` needs no script:
`audit.yml` repeats the two commands of `parser.yml`.

It is one Python file using only the standard library, like `scripts/grading/`. Tests: `test_audit.py` (standard
library `unittest`, real reports in `tests/fixtures/audit/`: a clean run of this repo and one vulnerable package each).
Lint: the parser's ruff rules and pin (`ruff.toml`).

```bash
python3 -I -m unittest discover -s scripts/audit -v
uvx ruff@0.17.2 check --config scripts/audit/ruff.toml scripts/audit
uvx ruff@0.17.2 format --check --config scripts/audit/ruff.toml scripts/audit
```

## Running it

```bash
dotnet list UmeRag.slnx package --vulnerable --include-transitive --format json > /tmp/dotnet.json
python3 -I scripts/audit/audit.py dotnet /tmp/dotnet.json

cd src/admin-ui && npm ci && npm audit --omit=dev --audit-level=low && { npm audit --json > /tmp/npm.json || true; }
python3 -I ../../scripts/audit/audit.py npm /tmp/npm.json
```

Exit 0 when clean, 1 otherwise, with one line per finding. `--summary FILE` appends a short Markdown summary (the
workflows pass `$GITHUB_STEP_SUMMARY`).

## npm exceptions

`npm-audit-exceptions.txt` holds the dated exceptions, one line each: `<GHSA id> <last day, YYYY-MM-DD> <reason>`.
They apply only to the all-dependencies audit, so only to dev-only advisories (production dependencies are audited
first, with no exceptions), and only for a fix that can't land yet (issue #61: lint toolchain fixes younger than 7
days). After its last day an exception no longer applies and both workflows turn red until the fix lands. A malformed
line fails the check. .NET and Python have no exceptions.
