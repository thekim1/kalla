# Finished queue items up to v0.8.0

The ticked items of the work queues, moved here unchanged when `v0.8.0` was released (2026-11-18, session 48).
History: it does not bind. An item is found by its ID (`A-27`); the queues in `plans/work/` hold only open and waiting
items.

## Stream A (`stream-a.md`)

### [x] A-20 Pin GitHub Actions by commit SHA (#506)
- **Do:** every `uses:` in `.github/workflows/*.yml` pinned to a full commit SHA with the version in a comment, checked
  against the action's release; optionally `zizmor` in `audit.yml`.
- **Paths:** `.github/**`.
- **Done when:** no workflow references an action by tag; `verify.yml` green.
- **Waits on:** postponed by the user (2026-11-08) until before go-live, with the 7-day package rule's return; A-27
  follows it.
- **Read:** #506; repo-and-deployment §8.

### [x] A-27 Image CVE scan and automated pin bumps (#507)
- **Do:** a scheduled scan of the built images in `audit.yml` (fixable High/Critical fail it), and Renovate or Dependabot
  for image digests, NuGet, npm, uv and Actions with a minimum release age of 7 days (the 7-day rule).
- **Paths:** `.github/**`, `images/**`.
- **Done when:** the first scan report and the first bump PR exist.
- **Waits on:** A-20.
- **Read:** #507; AGENTS.md "7-day package rule".
