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

export interface CollectionSummary {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  publishedAt: IsoDateTime | null
}

/** An assistant that links the collection, through its published version, its draft, or both. */
export interface CollectionUser {
  id: string
  name: string
  classification: Level
  published: boolean
  draft: boolean
}

export interface Collection extends CollectionSummary {
  ownerTeamId: string | null
  embeddingProfileId: string
  retrievalSettings: Record<string, unknown>
  usedBy: CollectionUser[]
  updatedAt: IsoDateTime
  updatedBy: string | null
  version: string
}

export interface CollectionCreate {
  name: string
  departmentId: string
  ownerTeamId: string | null
  classification: Level
}

export interface SourceSummary {
  id: string
  collectionId: string
  type: SourceType
  name: string
  classification: Level
  state: string
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
  startedAt: IsoDateTime
  finishedAt: IsoDateTime | null
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

/** A crawl run's report (worker `CrawlSourceRunner`): completeness, the verdict, removal checks and demote counts. */
export interface CrawlReport {
  completeness?: { complete: boolean; reasons: string[]; seeds: number; seedsResolved: number; seedsFailed: number; incremental: boolean }
  verdict?: { runFailure: string | null; runDegradation: string | null }
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
  }[]
  truncated: boolean
}

// ---------- Assistants (admin design §4.5, §4.9) ----------
export interface AssistantSummary {
  id: string
  slug: string
  name: string
  departmentId: string
  classification: Level
  state: string
  published: boolean
  hasDraft: boolean
}

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
  draft: { number: number; updatedAt: IsoDateTime; baseChanged: boolean; version: string } | null
  audienceClientIds: string[]
  updatedAt: IsoDateTime
  updatedBy: string | null
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

export interface AssistantDraft {
  number: number
  gatewayModel: string | null
  systemPrompt: string
  querySteps: Record<string, unknown>
  notFound: Record<string, unknown>
  collectionIds: string[]
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
}

export interface GatewayModel {
  name: string
  kind: string
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
