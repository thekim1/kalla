/** The admin API's types (Ume.Rag.AdminApi). JSON is camelCase; dates are ISO-8601 with offset. */

/** ISO-8601 timestamp with offset. */
export type IsoDateTime = string

/** Admin roles (identity design §1.3), as `Ume.Rag.Domain.Admin.RagRoles`. */
export const ROLES = [
  'rag-platform-admin',
  'rag-department-admin',
  'rag-pipeline-editor',
  'rag-reviewer',
  'rag-skill-editor',
  'rag-editor',
  'rag-viewer',
] as const
export type Role = (typeof ROLES)[number]

/** RFC 9457 problem details; admin faults add `code` and `params`, validation faults `errorCodes` (admin design §4.2). */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  errors?: Record<string, string[]>
  code?: string
  params?: Record<string, string>
  errorCodes?: Record<string, { code: string; params?: Record<string, string> }[]>
  [extension: string]: unknown
}

// ---------- BFF ----------
export type DepartmentSource = 'claim' | 'group'

export interface BffUser {
  isAuthenticated: boolean
  /** ASP.NET Core environment name of the admin API, for example Development or Production. */
  environment?: string
  name: string | null
  email: string | null
  roles: string[]
  departmentCodes: string[]
  /** Where each department came from: the department claim, or a member group (identity design §1.5). */
  departmentSources: { code: string; source: DepartmentSource }[]
  teamCodes?: string[]
  sessionExpiresAt: IsoDateTime | null
  /** The session's absolute end (Admin:MaxSessionHours after sign-in); extending never goes past it. */
  sessionLimitAt?: IsoDateTime | null
}

// ---------- Organisation ----------
export interface Team {
  id: string
  name: string
  code: string | null
  isActive: boolean
}

export interface Department {
  id: string
  name: string
  costCenterCode: string
  isActive: boolean
  syncedAt: IsoDateTime
  teams: Team[]
}

export interface OrgSyncResult {
  departments: number
  teams: number
}

export interface OrgSyncStatus {
  enabled: boolean
  lastRunAt: IsoDateTime | null
  lastSuccessAt: IsoDateTime | null
  consecutiveFailures: number
  lastError: string | null
  lastResult: OrgSyncResult | null
}

// ---------- Collections, sources, documents (admin design §4.5) ----------
export type Level = 'public' | 'internal' | 'sensitive'
export type SourceType = 'url-list' | 'crawl' | 'upload' | 'sharepoint'

export interface Page<T> {
  items: T[]
  total: number
}

export interface KeysetPage<T, K = string> {
  items: T[]
  next: K | null
}

/** Who may link a knowledge base besides its own department (admin design §3.2). */
export type SharingLevel = 'department' | 'selected' | 'municipality'

interface CollectionBase {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  publishedAt: IsoDateTime | null
  sharedWith: SharingLevel
  contactName: string | null
  contactEmail: string | null
}

/**
 * `GET /api/collections?view=` rows. For another department's knowledge base (`shared`) this is all a department it is
 * shared with sees: name, owner, level, contact, document count and topics (admin design §4.5).
 */
export interface CollectionSummary extends CollectionBase {
  departmentName: string
  shared: boolean
  documentCount: number
  topics: string[]
}

export type CollectionView = 'mine' | 'shared' | 'all'

/** An assistant that links the collection, through its published version, its draft, or both; archived ones too. */
export interface CollectionUser {
  id: string
  name: string
  classification: Level
  state: 'active' | 'archived' | string
  published: boolean
  draft: boolean
  departmentId: string
  departmentName: string
  /** The link's topics; empty = the whole knowledge base. */
  topics: string[]
}

/** The owner's detail. */
export interface Collection extends CollectionBase {
  shared: false
  ownerTeamId: string | null
  embeddingProfileId: string
  retrievalSettings: Record<string, unknown>
  /** Only with `selected`. */
  sharedWithDepartmentIds: string[]
  /** Live documents (they can be found), the same count as the list row's. */
  documentCount: number
  usedBy: CollectionUser[]
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/** `GET /api/collections/{id}`: the owner's detail, or the shared summary for a department it is shared with. */
export type CollectionRead = Collection | (CollectionSummary & { shared: true })

export interface CollectionCreate {
  name: string
  departmentId: string
  classification: Level
  contactName: string | null
  contactEmail: string | null
}

/** `GET /api/collections/{id}/topics`: `rules` only for the owning department. */
export interface Topic {
  tag: string
  documents: number
  rules: { sourceId: string; sourceName: string; on: string; match: string }[] | null
}

/** One row of the Map (admin design §5.5.6): department → knowledge base → assistant → audience. */
export interface MapRow {
  collection: {
    id: string; name: string; departmentId: string; departmentName: string; shared: boolean; sharedWith: SharingLevel; classification: Level
    published: boolean; contactName: string | null; contactEmail: string | null
  }
  assistant: {
    id: string; name: string; state: string; publishedVersion: number | null; draftOnly: boolean; topics: string[]; contactName: string | null
    contactEmail: string | null
  } | null
  audience: { clientId: string; name: string; kind: string } | null
}

export interface DepartmentMap {
  departmentId: string
  departmentName: string
  rows: MapRow[]
}

export interface SourceSummary {
  id: string
  collectionId: string
  type: SourceType
  name: string
  classification: Level
  state: string
}

/** `GET /api/collections/{id}/sources`: a source with its document count and latest run. */
export interface SourceListItem extends SourceSummary {
  documents: number
  lastRun: { id: string; state: string; startedAt: IsoDateTime; finishedAt: IsoDateTime | null } | null
}

export interface DocumentSummary {
  id: string
  sourceId: string
  title: string | null
  url: string | null
  displayName: string | null
  mediaType: string | null
  parseOutcome: string | null
  piiFlagged: boolean
  piiCleared: boolean
  held: boolean
  live: boolean
  sourceModifiedAt: IsoDateTime | null
  fetchedAt: IsoDateTime | null
  lastSeenAt: IsoDateTime
}

export interface Source extends SourceSummary {
  settings: Record<string, unknown>
  schedule: string | null
  pipelineId: string
  containsThirdPartyText: boolean
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/** `POST /api/sources/{id}/impact`: what a settings change does; `impactToken` when a scope or identity change needs confirming (V25). */
export interface SourceImpact {
  effects: ('identity' | 'scope' | 'behaviour' | 'cosmetic' | string)[]
  liveDocuments: number
  usedBy: CollectionUser[]
  impactToken: string | null
}

export interface SourceUpdate {
  name: string
  classification: Level
  settings: Record<string, unknown>
  schedule: string | null
  impactToken?: string
  version: string
}

/** `GET /api/runs/{id}/deletions`: a document the run proposes to delete (connectors §13). */
export interface DeletionProposal {
  documentId: string
  title: string | null
  url: string | null
  reason: string
  exempt: boolean
  decision: string | null
  lastSeenAt: IsoDateTime
}

/** `GET /api/sources/{id}/held`: a document kept by a rejected deletion. */
export interface HeldDocument {
  id: string
  title: string | null
  url: string | null
  displayName: string | null
  heldAt: IsoDateTime
  heldBy: string | null
  lastSeenAt: IsoDateTime
}

/** `GET /api/sources/{id}/files`: an upload slot. */
export interface UploadFile {
  id: string
  fileName: string
  bytes: number
  state: 'received' | 'clean' | 'malware' | 'refused' | 'indexed' | 'deleted' | string
  reason: string | null
  documentId: string | null
  uploadedBy: string | null
  uploadedAt: IsoDateTime
  replacedAt: IsoDateTime | null
  deletedAt: IsoDateTime | null
}

/** `GET /api/sources/{id}/quarantine`: metadata only (connectors §11). */
export interface QuarantinedFile {
  id: string
  origin: 'upload' | 'crawl' | string
  name: string | null
  url: string | null
  signature: string
  runId: string | null
  detectedAt: IsoDateTime
  purgeAfter: IsoDateTime
}

export interface UploadBatch {
  batchId: string
  maxFiles: number
  maxBytes: number
}

export interface UploadedFile {
  n: number
  uploadId: string
  operation: 'add' | 'replace' | string
  fileName: string
  bytes: number
  sha256: string
}

export interface SourceCreate {
  type: SourceType
  name: string
  classification: Level
  settings: Record<string, unknown>
}

export interface SyncStarted {
  jobId: string | null
  runId: string | null
  alreadyRunning: boolean
  kind: string | null
}

/** A job the UI polls (admin design §4.7): `state` queued, running, succeeded, failed, dead or cancelled. */
export interface Job<TResult = unknown> {
  id: string
  class: string
  kind: string | null
  state: string
  enqueuedAt: IsoDateTime
  completedAt: IsoDateTime | null
  runId: string | null
  result: TResult | null
  lastError: string | null
}

export type CheckState = 'pass' | 'warn' | 'fail'

export interface PreflightResult {
  state: CheckState
  checks: { check: string; state: CheckState; code: string; target?: string | null }[]
  estimatedPages?: number | null
  cms?: string | null
}

/** `GET /api/pipelines?collectionId&sourceType`: a pipeline a new source can use; `isDefault` is the one it gets without a choice. */
export interface PipelineSummary {
  id: string
  name: string
  templateId: string
  departmentId: string | null
  publishedVersionId: string | null
  isDefault: boolean
}

export interface PipelineVersion {
  id: string
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  definition: { schema?: number; template: string; templateVersion: number; params: Record<string, unknown> }
  publishedAt: IsoDateTime | null
}

/** `GET /api/pipelines/{id}`: the pipeline with its versions, newest first. */
export interface Pipeline {
  id: string
  name: string
  templateId: string
  departmentId: string | null
  publishedVersionId: string | null
  versions: PipelineVersion[]
  version: string
}

/** contracts/jobs/v1/preview.publish-plan.result.schema.json */
export interface PublishPlan {
  pipelineVersionId: string
  documents: number
  sources: { sourceId: string; documents: number }[]
  fromStage?: string
  parserPages?: number
  embeddingTokens?: number
  estimatedMinutes?: number
  estimatedCostUsd?: number
}

/** `GET /api/publications/{id}`: the plan job, its draft and the pipeline version the confirm sends. */
export interface Publication {
  id: string
  state: string
  plan: PublishPlan | null
  lastError: string | null
  pipelineId: string
  pipelineVersionId: string
  draftNumber: number | null
  draftState: string | null
  version: string
  published: boolean
  superseded: boolean
}

export interface PublicationStarted {
  publicationId: string
  pipelineId: string
  draftVersionId: string
  draftNumber: number
  copiedOnWrite: boolean
}

export interface EvaluationRun {
  id: string
  kind: string
  variant: string
  questionCount: number | null
  k: number
  found: number | null
  recallAtK: number | null
  mrr: number | null
  wins: number | null
  losses: number | null
  comparedRunId: string | null
  candidate: boolean
  /** The worker's coverage-threshold proposal (processing design §13.6), as stored; null without human questions. */
  thresholdProposal: CoverageProposal | null
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
}

/** A proposed threshold and how many human questions it misclassifies. */
export interface ThresholdProposal {
  value: number
  misclassified: number
}

/** `EvaluationRuns.ThresholdProposal` (admin design §7.1): the human layer's not-found counts and the proposed thresholds. */
export interface CoverageProposal {
  answerable: number
  unanswerable: number
  falseNotFound?: number
  unanswerableNotFound?: number
  coverageMaxDistance?: ThresholdProposal | null
  coverageMinKeywordScore?: ThresholdProposal | null
}

/** `GET /api/evaluation-runs/{id}/results`: one question's rank in the run and in the compared run. */
export interface EvaluationResult {
  questionId: string
  question: string
  layer: 'generated' | 'human' | string
  rank: number | null
  previousRank: number | null
  /** Null for an unanswerable question (no expected documents). */
  hit: boolean | null
  coverage: 'ok' | 'low' | string | null
  bestDistance: number | null
  bestKeywordScore: number | null
}

export interface EvaluationQuestion {
  id: string
  layer: 'generated' | 'human' | string
  state: 'active' | 'stale' | 'retired' | string
  question: string
  expectedAnswer: string | null
  expectedDocumentIds: string[]
  origin: string | null
  createdBy: string | null
  retiredAt: IsoDateTime | null
}

export interface EvaluationQuestionInput {
  question: string
  expectedAnswer: string | null
  expectedDocumentIds: string[]
}

/** `GET /api/collections/{id}/evaluation/generate`: what generating test questions would do now. */
export interface GenerationForecast {
  kind: 'generate-set' | 'top-up-set' | string
  model: string | null
  judgeModel: string | null
  liveDocuments: number
  eligibleChunks: number
  activeQuestions: number
  target: number
  needed: number
  modelCalls: number
  estimatedTokens: number
  estimatedCostUsd: number | null
}

/** `GET /api/documents/{id}`, the fields the admin UI reads. */
export interface DocumentDetail {
  id: string
  sourceId: string
  title: string | null
  url: string | null
  displayName: string | null
}

export interface EvaluationOverview {
  hasSet: boolean
  questions: { layer: string; state: string; count: number }[]
  latestRuns: EvaluationRun[]
}

export interface TemplateDetail {
  id: string
  version: number
  sourceType: string
  deprecated: boolean
  stages: string[]
  paramsSchema: { properties?: Record<string, Record<string, unknown>> }
}

export type PreviewStage = 'fetch' | 'scan' | 'detect' | 'parse' | 'clean' | 'chunk' | 'personal-data' | 'embed' | 'index'

/** contracts/jobs/v1/preview.stage-preview.result.schema.json */
export interface PreviewStageResult {
  stage: PreviewStage
  state: 'succeeded' | 'failed' | 'skipped'
  durationMs?: number
  cacheHit?: boolean
  facts?: Record<string, unknown> | null
  estimatedCostUsd?: number | null
  costUsd?: number | null
  error?: string | null
  output?: string | null
  truncated?: boolean
}

export interface PreviewSiteProfile {
  host: string
  origin: 'manual' | 'preset'
  siteProfileVersionId?: string | null
  presetId?: string | null
  before: string
  after: string
}

export interface PreviewDocument {
  documentId?: string | null
  url?: string | null
  uploadId?: string | null
  stages: PreviewStageResult[]
  siteProfile?: PreviewSiteProfile | null
}

export interface StagePreviewResult {
  documents: PreviewDocument[]
  expiresAt: IsoDateTime
}

/** A preview output blob: the stage's value as the worker shows it, cut at 2 MB with `truncated`. */
export interface PreviewOutput<T> {
  stage: string
  truncated: boolean
  value: T | null
}

export interface PreviewBlock {
  kind: string
  text: string
  level?: number | null
  layer?: string
}

/** The parse and clean outputs (and the site profile's before/after): the extracted blocks and facts. */
export interface PreviewParsed {
  title?: string | null
  blocks: PreviewBlock[]
  breadcrumb?: string[]
  visibleDate?: string | null
  documentDate?: string | null
}

export interface PreviewChunks {
  chunks: { ordinal: number; prefix: string; headingPath: string; text: string; tokenCount: number; page?: number | null }[]
}

export interface PreviewPii {
  counts: Record<string, number | string | null>
  typesFired: string[]
  state: string
}

export interface RunSummary {
  id: string
  kind: string
  trigger: string
  state: string
  phase: string | null
  added: number
  updated: number
  unchanged: number
  deleted: number
  failed: number
  proposedDeletions: number
  costUsd: number
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
}

/**
 * A scope host crawled with a non-default politeness profile (worker `PolitenessReportEntry`, connectors design §6.6):
 * its preset and only the members that differ from its kind's defaults (null otherwise).
 */
export interface PolitenessReportEntry {
  host: string
  preset: string
  requestsPerSecond: number | null
  maxConnections: number | null
  crawlDelay: 'honour' | 'ignore' | null
  adaptToLatency: boolean | null
  crawlWindow: 'source' | 'any' | null
  maxRetryAfterSeconds: number | null
  maxBackoffFactor: number | null
  breakerOff: boolean | null
  breakerThreshold: number | null
  breakerPauseMinutes: number | null
  robotsIgnoredReason: string | null
  userAgent: string | null
  renderPerHost: number | null
}

/** A crawl run's report (worker `CrawlSourceRunner`): completeness, the verdict, removal checks, demote counts and non-default politeness. */
export interface CrawlReport {
  completeness?: { complete: boolean; reasons: string[]; seeds: number; seedsResolved: number; seedsFailed: number; incremental: boolean }
  verdict?: { runFailure: string | null; runDegradation: string | null }
  politeness?: PolitenessReportEntry[]
  [section: string]: unknown
}

export interface Run {
  run: RunSummary
  sourceId: string
  pipelineVersionId: string | null
  scheduledFor: IsoDateTime | null
  report: CrawlReport | null
}

export interface RunItem {
  id: number
  createdAt: IsoDateTime
  documentId: string | null
  identity: string
  outcome: string
  reason: string | null
  strategy: string | null
  durationMs: number | null
  costUsd: number | null
}

export interface PiiReport {
  applies: boolean
  piiMode: string
  collectionClassification: Level
  documents: {
    documentId: string
    title: string | null
    url: string | null
    displayName: string | null
    state: 'held' | 'live'
    previousVersionLive: boolean
    counts: Record<string, number>
    rulesFired: number
    /** The types whose flag rule fired, as the worker stored them (#217): type names only, never values. */
    typesFired: string[]
  }[]
  truncated: boolean
}

// ---------- Assistants (admin design §4.5, §4.9) ----------
/** `QuerySteps.answer.generalKnowledge` (chat design §7.3, admin design §5.5.4). */
export type GeneralKnowledge = 'off' | 'supplement' | 'fallback' | 'open'

export interface AssistantSummary {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  state: string
  published: boolean
  hasDraft: boolean
  /** The published version's level; null before the first publish. */
  generalKnowledge: GeneralKnowledge | null
}

/** An assistant's reranker (`QuerySteps.retrieval.rerank`, B-16); absent is off. */
export type Reranker = 'chat' | 'gateway'

export interface Assistant {
  id: string
  slug: string
  name: string
  description: string | null
  contactName: string | null
  contactEmail: string | null
  departmentId: string
  ownerTeamId: string | null
  classification: Level
  historyMode: string
  retentionDays: number
  state: string
  publishedVersion: number | null
  /** The published version's level; null before the first publish. */
  generalKnowledge: GeneralKnowledge | null
  /** The rerankers the platform has a model for (`chat`, `gateway`); empty when it has none, which is the usual setup. */
  rerankers: Reranker[]
  draft: { number: number; updatedAt: IsoDateTime; baseChanged: boolean; version: string } | null
  audienceClientIds: string[]
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

/** `GET /api/assistants/{id}/versions`, newest first. */
export interface AssistantVersionSummary {
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  createdAt: IsoDateTime
  createdBy: string | null
  publishedAt: IsoDateTime | null
  publishedBy: string | null
  restoredFrom: number | null
}

/** `GET /api/data-protection/{level}`: what a level allows (plan §8a). */
export interface DataProtectionPolicy {
  classification: Level
  allowedHistoryModes: string[]
  defaultHistoryMode: string
  retentionDefaultDays: number
  retentionMaxDays: number
  version: string
}

export interface AssistantCreate {
  name: string
  departmentId: string
  classification: Level
  description: string | null
  contactName: string | null
  contactEmail: string | null
}

export interface RuleWarning {
  code: string
  params: Record<string, string> | null
  affected: string[]
}

/** A collection an assistant version links; no tags = the whole collection (admin design §4.5). */
export interface CollectionLink {
  collectionId: string
  tags: string[]
}

/** `GET /api/assistants/{id}/versions/{n}`: one version's settings. */
export interface AssistantVersion {
  number: number
  state: 'draft' | 'published' | 'superseded' | string
  gatewayModel: string | null
  systemPrompt: string
  querySteps: Record<string, unknown>
  notFound: Record<string, unknown>
  collectionLinks: CollectionLink[]
  restoredFrom: number | null
  createdAt: IsoDateTime
  createdBy: string | null
  publishedAt: IsoDateTime | null
  publishedBy: string | null
}

export interface AssistantDraft {
  number: number
  gatewayModel: string | null
  systemPrompt: string
  querySteps: Record<string, unknown>
  notFound: Record<string, unknown>
  collectionLinks: CollectionLink[]
  warnings: RuleWarning[]
  updatedAt: IsoDateTime
  version: string
}

export interface ClientSummary {
  id: string
  name: string
  kind: string
  classificationCeiling: Level
  isEnabled: boolean
  /** The client's own UI (admin design §5.4 step 13). */
  portalUrl?: string | null
}

export interface GatewayModel {
  name: string
  /** `model` (a deployment) or `route` (a manual route alias). */
  kind: string
  /** What the model serves (data-model §2.1); route aliases are `chat`. Only chat models answer. */
  modelKind: 'chat' | 'embedding' | 'rerank'
  residencies: string[]
}

export interface AssistantTestResult {
  target: string
  versionNumber: number
  retrieval: {
    collections: { id: string; name: string; published: boolean }[]
    coverage: { status: string } | null
    degraded: string | null
    candidates: { rank: number; documentId: string; title: string; urlOrFileName: string | null; headingPath: string; text: string; cited: boolean }[]
  }
  condense: { used: boolean; outcome: string | null }
  answer: { raw: string | null; delivered: string; outcome: string; language: string; citations: { number: number; documentId: string; title: string }[] }
  gateway: { model: string; residency: string; latencyMs: number | null; inputTokens: number | null; outputTokens: number | null }
  durationMs: number
}

/** `GET /api/collections/{id}/setup`: the setup checklist computed from state (admin design §5.4). */
export interface CollectionSetup {
  collectionId: string
  name: string
  sources: number
  firstSyncDone: boolean
  syncRunning: boolean
  testQuestions: number
  evaluated: boolean
  evaluating: boolean
  published: boolean
  assistantId: string | null
  assistantPublished: boolean
  available: boolean
  done: boolean
}

/** `GET /api/overview`: whether any collection is in scope, and the checklists not done yet. */
export interface Overview {
  hasCollections: boolean
  setup: CollectionSetup[]
}

/** `GET /api/audit`: an entry of the audit log (admin design §9.1). */
export interface AuditSummary {
  id: number
  timestamp: IsoDateTime
  actor: string
  actorName: string | null
  action: string
  entityType: string
  entityId: string | null
  service: string | null
  departmentId: string | null
  classification: string | null
  configVersion: number | null
  reason: string | null
}

/** `GET /api/audit/{id}`: the entry with its before/after snapshot as JSON text. */
export interface AuditDetail extends AuditSummary {
  recordedAt: IsoDateTime
  actorRoles: string | null
  actorDepartments: string | null
  details: string | null
  sourceAddress: string | null
  requestId: string | null
  userAgent: string | null
}

/** `GET /api/feedback`: a rating without content (admin design §8.1). */
export interface FeedbackItem {
  id: string
  createdAt: IsoDateTime
  rating: number
  score: number | null
  reasonCategory: string | null
  reviewState: 'open' | 'reviewed' | 'dismissed' | string
  hasComment: boolean
  source: string
  assistantId: string
  assistantName: string
  assistantVersion: number | null
  clientId: string
  clientName: string | null
  outcome: string
  coverage: string | null
  language: string | null
  citedDocuments: { id: string; title: string | null }[]
}

/** `GET /api/feedback/summary`: the period's totals and the Open WebUI import health. */
export interface FeedbackSummary {
  totals: { answers: number; notFound: number; thumbsUp: number; thumbsDown: number }
  imports: { clientId: string; clientName: string; lastRunAt: IsoDateTime | null; lastOutcome: string | null; unmapped: number }[]
}

/** `POST /api/access/explain` (identity design §4.9): the first failing policy step and its stable reason. */
export interface AccessExplanation {
  allowed: boolean
  step: string
  reason: string
  status: number
  effectiveLevel: string | null
  maxResidency: string | null
  configVersion: number
}

export interface ExplainAccessRequest {
  clientId: string
  assistantId: string
  subject: { kind: string; id: string; departments?: string[]; groups?: string[] }
}
