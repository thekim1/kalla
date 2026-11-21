# repo-and-deployment: history moved out of the design

The process sections of [`../../platform/repo-and-deployment.md`](../../platform/repo-and-deployment.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 13. Deferred or removed (simplification pass)

See [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md) (section numbers there).

| Item | Where it went |
|---|---|
| `images/postgres` (pgvector build + Hunspell), its dictionary and per-backend memory tests (§3) | Removed: stock pgvector image, .NET decompounder; H1 closed |
| `pg_dump` scripts, `backup-blobs.sh`, the ≤ 30-day rule for every key backup, `reconcile-retention` (§4) | Replaced: pgBackRest with WAL and PITR, upload copy, two key classes with escrow |
| Tier SQL files, tier loop, tier test matrix, per-tier buckets, `Tier` log scope (§5) | Removed: separate deployment if ever needed (T1/K2) |
| `rag-tools` container, role `ume_rag_tools`, `tools`/`tools-egress` networks, `ToolHub__*`, its secrets (§10) | P2 hub in-process in `rag-api` and `rag-admin`; a separate service only with the first external, OBO or write tool (P3) |
| `tools/Ume.Rag.McpGen`, `Microsoft.OpenApi`; `Mcp:AskAssistantTimeout` (§10) | Dropped; P3 |
| Renderer image, resource and settings, `Microsoft.Playwright` | back in scope (user, 2026-11-07; §5.4, connectors design §8) |
| `PdfPig` (§1, §9) | removed (every PDF to the parser) |
| EasyOCR, `ocr_bakeoff.py`, `tesseract-ocr-eng`, `PARSER_OCR_DEFAULT`; `NER_LANES`; LibreOffice, `soffice-sandboxed`, `office.py`, `mail/` and `python-oxmsg` (§1) | Dropped; P2 with legacy Office, RTF and email |
| Vue Flow and the canvas budget; Open WebUI task model, SQL pruning and the pruning sidecar (§1, §2) | Dropped; generation off, feedback pruning via its API, chat copies open (W2) |
| `Gateway__ServiceKey`, `Gateway:AttributionMode`, `Gateway:SendResidencyCeiling`, interim keys; shared Entra registration (§6, §7) | Replaced: keys per level and deployable; RAG's own registrations |
| a/b slots, `deploy-e2e.yml`, `LeaderLeases`, `connection-budget.sh`, `ume_rag_monitor`, Redis leases, `Security__CrawlKeyPepper` (Redis-lease use only; the pepper itself is P1); `flake-hunt.yml`, `scale.yml`, Atheris; `OrgSync:MaxAgeHours` (§1, §7) | P2 |
| `perf.yml`, nightly `evaluation.yml` (§1) | P1, before the pilot |
| `redis` in the pilot: container, its `data` network membership and limits row, `ConnectionStrings__redis`, Redis password, ACL and certificate; Redis in the default AppHost (§14) | P2 (first multi-host deployment, enabled by `ConnectionStrings:redis`); P1 uses the in-memory `IRateLimitStore`/`ISharedCache` and the Postgres admin session store |
| `rag-pgbackrest` sidecar, `ume-rag/pgbackrest` image, `PGBACKREST_IMAGE`, `pgbackrest-spool` volume (§14) | Replaced: pgBackRest inside `ume-rag/postgres`, backups from a host timer |

## 12. Open decisions and contradictions found: how they were settled

Moved here from the design on 2026-11-21 (session 53's clean-up); the rows stay in the design, each in its current
state.

**Review (2026-10-29):** accepted 1, 2 (added to `gateway-changes` §3c), 3, 6, 7, 8, 9, 10 (as a proposal for both repos), 12, 13, 14.
User decisions: 5 Bicep, 11 postponed (file keys in the pilot, Key Vault later), 15 ClamAV direct without a mirror; 4 (self-hosted runner) waits for IT security.
Review fix: Chromium keeps its sandbox via a seccomp profile (§5.4). Rows 3, 6, 7, 8, 11 and 12 are restated in the
decided form of the simplification pass (2026-10-30). Row 8 first planned Open WebUI as a Compose project of its own
(`compose.openwebui.yaml`) with an `env_file` and an `openwebui` network; it was built as a profile of
`compose.rag.yaml` (M-17).
