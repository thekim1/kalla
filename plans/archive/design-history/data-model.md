# data-model: history moved out of the design

The process sections of [`../../platform/data-model.md`](../../platform/data-model.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 13. Deferred or removed (simplification pass)

See [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md) (section numbers there).

| Item | Where it went |
|---|---|
| Platform-wide document store, `DocumentSources`, shared-chunk rule, `CollectionIds` arrays + GIN, set revival, follow runs, `Jobs.RunIds`, trigger `shared-document` (§2) | Removed: per-source documents and chunk sets, scalar `Chunks.CollectionId` + B-tree, reuse by caches |
| Merkle stage hashes, the graph form of `PipelineVersions.Definition` (nodes, edges, locked nodes), the `publishing`/gating state and `PlanJobId` of `PipelineVersions`, `PipelineCanvasLayouts`, `NodeManifest.Ports`, `Pipelines.IsTemplate`/`CopiedFromId` (§2, §8) | Removed: per-stage cache keys; pipeline version = (template id, parameters); plan and report are job results |
| PII flag and counts on `Documents` (§2) | Moved to `ChunkSets`; clearance stays on `Documents` |
| `Variant`, `RenderRecommendedAt`, render leases, the `:r` dedup suffix (§1) | Removed; the renderer (2026-11-07) adds only `Documents.FetchMode` |
| `Documents.ETag`/`LastModified` (§2) | Moved to `CrawlFrontier` (one request serves every source) |
| Hydration keeping only live-set chunks plus a batched follow-up that marks superseded chunks deleted (§9) | Replaced: chunks written with `DeletedAt` set and flipped in the swap transaction (§2.2) |
| `RetrievalSettings.efSearch` and per-query `SET LOCAL` (§9) | Replaced: role defaults `hnsw.ef_search`, `iterative_scan`, `max_scan_tuples` (§1.1) |
| Email attachment columns on `Documents` (§1) | P2 with email formats |
| `content.rag_swedish`, Hunspell in Postgres, custom image (§3) | Removed: .NET decompounder, built-in `swedish`; H1 closed |
| Tiers: resolver, `DataTiers`, `TierName`, main-only/tier-local placement, composite keys and FKs, tier SQL files, move-tier, cross-tier fusion (§5) | Removed: separate deployment if ever needed (T1/K2) |
| Per-conversation keys, epochs, `DeletedByUserAt` tombstone, key-to-message cascades, always-on shred buckets (§5) | Removed: bucket keys per (level, expiry month); shredding behind A1 |
| `DocumentKeys`, per-upload and per-job keys, `BlobPutMode.Overwrite`, per-tier containers (§4) | Removed: key in the blob header, `public` unencrypted, write-once keys, one container |
| Partitioned `Jobs`, advisory-lock dedup, queued-job moves; class `ner` (§9, §1) | Removed: unpartitioned `Jobs` with a partial unique index; NER inside the processing job |
| Gate columns on `Collections`, `EvaluationRuns` and `Settings`; parse auto-apply columns and states (§8, §2) | Removed: advisory harness; P2 proposals with approval |
| `GatewayKeyLinks`; `Clients.TaskModel`/`TaskModelResidency`; `ResolveUsersByEmail` and the `owui-oid` key; GIN on `MemberGroups` (§1, §6, §7) | Removed or dropped |
| Redis `ume-rag:groups:{oid}` (§7) | P2 with the Graph member-group lookup (identity design) |
| Redis in the pilot: `ume-rag:admin-session:{sessionId}`, rate-limit and query-embedding keys (§14) | Admin sessions in `config."AdminSessions"` (also in P2); rate limits and the query-embedding cache in process in P1; Redis from the first multi-host deployment (P2) |
| Skill pin/follow columns (`AssistantSkills.PinnedVersionId`, `PinnedReason`), `SkillVersions` states `gating`/`gate-failed`, `Settings.Tools.SkillRoutingFloor`; `SkillAudiences`; `Assistants.RiskAcceptance` (§10) | Removed (P2 sharing through `CollectionShares`/`SkillShares`) |
| Tool hosting `municipal`/`external`, auth `none`/`api-key`/`obo`, `ToolServers.EncryptedCredential`, `PerCallConnection`; role `ume_rag_tools` (§10) | P3 (with `rag-tools`, the first external, OBO or write tool) |
| `ume_rag_monitor`, `LeaderLeases`, connection-budget script, batched dequeue, debounced `NOTIFY`, Redis crawl and embedding leases (§1) | P2 (first multi-host deployment) |
| 10-second fail-closed config rule (§9) | Replaced by the grace window (§5) |
| `Assistants.Mode` (`rag`/`agent`) as an editable column (§15) | Removed: derived from the published version's tool-bearing skills (§2.1) |
| `AssistantCollections`, `AssistantSkills`; assistant settings edited in place (§15) | Replaced: `AssistantVersions` (draft, published, superseded) with linked collections and skills as a snapshot, the view `PublishedAssistantVersions` for the api |
| `SkillVersionLinks` kind `collection` and the skill level rule over collections (§15) | Removed: skills carry know-how and tools only; an assistant's knowledge is its direct collections |

## 15. Deferred or removed (weakness review)

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer). The schema stays as designed; these tables and columns are built (or first written) in P2:

| Item | Where it went |
|---|---|
| `AssistantTestQuestions`, `AssistantTemplates`; `EvaluationRuns` kinds `answers` and `assistant-tests` | Built in P2 |
| `CrawlClaims` and the shared (level, URL) key of `CrawlFrontier` | Built in P2; P1 keys the frontier per source |
| Learning columns of `SiteProfiles`/`SiteProfileVersions` (`LastLearnedAt`, `NextLearnAfter`, `LearnedFromRunId`, `Sample`, states `proposed`/`rejected`, origin `learned`) | Written from P2; P1 stores `manual` versions only |

Per [weakness review §4](../passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected). The schema stays as designed; these are built (or first written) when A1 allows stored chat content:

| Item | Where it went |
|---|---|
| `chat."DataKeys"`, every chat ciphertext column and its `DataKeyId`/`TitleDataKeyId` | Built when A1 allows stored content; P1 writes metadata and ratings only (history mode `none`, §2.3, §7) |
| `DataProtectionPolicies` history modes `stored`/`session`, `ChatMasterKeyName` keys `rag-chat-<level>` | Same; P1 allows only `none` |
| `audit."ConversationAccess"` (conversation review, comment reveals) | Same |
| `Settings.ChatShreddingEnabled` and shred keys | Same (still also behind A1's answer on deletion) |

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `Departments.MemberGroups` and `Teams.Code` filled in P1 | P2, from gateway release 2; the columns stay and are empty (or null) until then |

## 12. Open decisions and contradictions found: how they were settled

Moved here from the design on 2026-11-21 (session 53's clean-up); the rows stay in the design.

**Review (2026-10-29):** recommendations 1–22 accepted and carried into the plans (platform §3, §8, §8a; worker §2,
§4; knowledge §3; key prefixes everywhere). 23 is resolved by the Chat API detail design (§15.2 there). Rows 5–8, 10,
13, 16, 18 and 19 are restated in the decided form of the simplification pass (2026-10-30); 24–25 come from its §15
(many assistants per department).
