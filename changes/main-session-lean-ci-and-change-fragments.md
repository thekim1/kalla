- **Release notes are files** (`changes/`): a pull request adds `changes/<branch>.md` instead of appending to
  *Unreleased*, so two pull requests no longer conflict there; `scripts/unreleased.py` checks the notes and folds them
  in at a release. **CI:** the backend job also skips changes only to the parser, the renderer image, `infra/`, the
  other workflows and the scripts it does not run.
