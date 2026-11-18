# Dependency audits

`audit.py` checks the reports of `dotnet list package --vulnerable` and `npm audit`, for the `backend` and `admin-ui`
jobs of `.github/workflows/verify.yml` (each pull request that changes code) and for `.github/workflows/audit.yml`
(daily against `main` and from the Actions tab), so a new advisory against an unchanged `main` shows up the next
morning. In `audit.yml` it also reads Grype's reports of the last release's images (the `images` job). Both workflows call this one file, so the checks cannot drift. The parser's `pip-audit` needs no script:
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

cd src/admin-ui && npm ci && { npm audit --json > /tmp/npm.json || true; }
python3 -I ../../scripts/audit/audit.py npm /tmp/npm.json
```

Exit 0 when clean, 1 otherwise, with one line per finding. `--summary FILE` appends a short Markdown summary (the
workflows pass `$GITHUB_STEP_SUMMARY`).

## npm exceptions

`npm-audit-exceptions.txt` holds the dated exceptions for the all-dependencies audit, one line each: `<GHSA id> <last
day, YYYY-MM-DD> <reason>`. Only for dev-only advisories whose fix can't land yet (issue #61: lint toolchain fixes
younger than 7 days). After its last day an exception no longer applies and both workflows turn red until the fix
lands. A malformed line fails the check.

`npm-production-exceptions.txt` holds the production audit's, one line each: `<GHSA id> <package> <last day,
YYYY-MM-DD> <reason>`. Only for an advisory with no fixed version, against a package the built bundle doesn't contain
(#853: picomatch, which vue-router declares for its build-time plugin). `audit.py npm-production` reads the bundle's
packages from the source maps of a second build (`vite build --sourcemap hidden --outDir …`), and fails when an
excepted package is in it, or when there are no source maps. An expired exception, and one that matched no advisory,
are workflow warnings. A current production exception also applies to the all-dependencies audit. .NET and Python
have no exceptions.

```bash
cd src/admin-ui && npm ci
npm audit --omit=dev --json > /tmp/prod.json || true
npx vite build --sourcemap hidden --outDir /tmp/bundle-modules --emptyOutDir
python3 -I ../../scripts/audit/audit.py npm-production /tmp/prod.json --modules /tmp/bundle-modules
```

## Image scan exceptions

`image-scan-exceptions.txt` holds the image scan's dated exceptions, one line each: `<vulnerability id> <image>
<package> <last day, YYYY-MM-DD> <reason>`, matched on all three of id, image (`parser`, `renderer`, …) and package
name as Grype reports it. Only for a High or Critical finding with a fix that no release can take yet and whose
vulnerable code the image cannot reach (#818: FFmpeg inside the opencv wheel, which nothing calls). An excepted finding
is still counted in the summary. After its last day the exception no longer applies; an expired one, and one that
matched no finding in its image, are printed as workflow warnings. A malformed line fails the check.

```bash
grype registry:ghcr.io/thekim1/ume-rag/parser:0.7.0 -o json --file /tmp/parser.json -q
python3 -I scripts/audit/audit.py grype parser=/tmp/parser.json
```
