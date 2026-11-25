# Unreleased change notes

A pull request that belongs in the release notes adds its own file here instead of editing `CHANGELOG.md`, and one for
the upgrade guide when an operator has something to know or do. The file names come from the branch, so two pull
requests never write the same lines:

| File | Folded into |
|---|---|
| `changes/<branch, "/" as "-">.md` | `CHANGELOG.md`, *Unreleased* |
| `changes/<branch, "/" as "-">.upgrade.md` | `docs/upgrade-guide.md`, *Unreleased* |

Each file holds one or more Markdown bullets as they would stand under *Unreleased* (`- **What changed** (#issue): ...`),
no headings, wrapped at 120 characters. `scripts/unreleased.py check` runs in `verify.yml`'s guard job; `show`
prints what the next release gets. At the release preparation the main session runs `scripts/unreleased.py fold`, which
appends the notes in the order they were added and deletes them.
