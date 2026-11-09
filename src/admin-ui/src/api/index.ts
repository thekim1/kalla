import { buildUrl, http, postLogout, request } from './client'
import type {
  CollectionLink,
  CollectionUpdate,
  AssistantUpdate,
  CollectionRead,
  CollectionView,
  DepartmentMap,
  SharingLevel,
  Topic,
  Assistant,
  AssistantCreate,
  AssistantDraft,
  AssistantSummary,
  AssistantTestResult,
  AssistantVersion,
  AssistantVersionSummary,
  BffUser,
  ClientSummary,
  GatewayModel,
  AccessExplanation,
  AuditDetail,
  ExplainAccessRequest,
  FeedbackItem,
  FeedbackSummary,
  AuditSummary,
  Collection,
  CollectionSetup,
  Overview,
  CollectionCreate,
  CollectionSummary,
  Department,
  DataProtectionPolicy,
  DocumentSummary,
  KeysetPage,
  OrgSyncResult,
  Job,
  OrgSyncStatus,
  Page,
  EvaluationOverview,
  EvaluationQuestion,
  EvaluationQuestionInput,
  EvaluationResult,
  EvaluationRun,
  GenerationForecast,
  DocumentDetail,
  Pipeline,
  Publication,
  PublicationStarted,
  TemplateDetail,
  PipelineSummary,
  PreviewOutput,
  PiiReport,
  Run,
  RunItem,
  RunSummary,
  Source,
  SourceCreate,
  DeletionProposalPage,
  HeldDocument,
  QuarantinedFile,
  UploadBatch,
  UploadedFile,
  UploadFile,
  SourceImpact,
  SourceUpdate,
  SourceListItem,
  SyncStarted,
} from './types'

/** The admin API, one function per endpoint (adapted from the gateway's api/index.ts). */
export const api = {
  bff: {
    user: () => http.get<BffUser>('/bff/user'),
    logout: () => postLogout(),
    extendSession: () => http.post<{ sessionExpiresAt: string; sessionLimitAt: string | null }>('/bff/session/extend'),
    loginUrl: (returnUrl: string) => buildUrl('/bff/login', { returnUrl }),
  },
  organisation: {
    departments: () => http.get<Department[]>('/api/organisation/departments'),
    syncStatus: () => http.get<OrgSyncStatus>('/api/organisation/sync'),
    sync: () => http.post<OrgSyncResult>('/api/organisation/sync'),
    departmentNames: () => http.get<{ id: string; name: string }[]>('/api/organisation/departments/names'),
    map: (id: string) => http.get<DepartmentMap>(`/api/organisation/departments/${id}/map`),
  },
  collections: {
    list: (query: { q?: string; page?: number; pageSize?: number; view?: CollectionView } = {}) => http.get<Page<CollectionSummary>>('/api/collections', query),
    get: (id: string) => http.get<CollectionRead>(`/api/collections/${id}`),
    topics: (id: string) => http.get<{ collectionId: string; topics: Topic[] }>(`/api/collections/${id}/topics`),
    sharing: (id: string, body: { sharedWith: SharingLevel; departmentIds: string[]; version: string }) =>
      http.put<Collection>(`/api/collections/${id}/sharing`, body),
    create: (body: CollectionCreate) => http.post<Collection>('/api/collections', body),
    publish: (id: string, version: string) => http.post<Collection>(`/api/collections/${id}/publish`, { version }),
    update: (id: string, body: CollectionUpdate) => http.put<Collection>(`/api/collections/${id}`, body),
    sources: (id: string) => http.get<SourceListItem[]>(`/api/collections/${id}/sources`),
    evaluation: (id: string) => http.get<EvaluationOverview>(`/api/collections/${id}/evaluation`),
    retrievalSettings: (id: string, settings: Record<string, unknown>, version: string) =>
      http.put<Collection>(`/api/collections/${id}/retrieval-settings`, { settings, version }),
    setup: (id: string) => http.get<CollectionSetup>(`/api/collections/${id}/setup`),
    addSource: (id: string, body: SourceCreate) => http.post<Source>(`/api/collections/${id}/sources`, body),
  },
  sources: {
    get: (id: string) => http.get<Source>(`/api/sources/${id}`),
    sync: (id: string) => http.post<SyncStarted>(`/api/sources/${id}/sync`),
    impact: (id: string, body: { settings: Record<string, unknown> }) => http.post<SourceImpact>(`/api/sources/${id}/impact`, body),
    update: (id: string, body: SourceUpdate) => http.put<Source>(`/api/sources/${id}`, body),
    runs: (id: string, after?: string) => http.get<KeysetPage<RunSummary>>(`/api/sources/${id}/runs`, { after, limit: 20 }),
    held: (id: string, after?: string) => http.get<KeysetPage<HeldDocument>>(`/api/sources/${id}/held`, { after, limit: 50 }),
    releaseHeld: (id: string) => http.post<{ jobId: string }>(`/api/sources/${id}/held/release`),
    files: (id: string, query: { deleted?: boolean; after?: string; limit?: number } = {}) =>
      http.get<KeysetPage<UploadFile>>(`/api/sources/${id}/files`, query),
    quarantine: (id: string, after?: string) => http.get<KeysetPage<QuarantinedFile>>(`/api/sources/${id}/quarantine`, { after, limit: 50 }),
    uploadBatch: (id: string) => http.post<UploadBatch>(`/api/sources/${id}/upload-batches`),
    /** Deleted files back within the 14-day grace (platform admin; connectors §9.3): a batch that re-adds the slots. */
    restoreFiles: (id: string, uploadIds: string[]) =>
      http.post<{ batchId: string; jobId: string; entries: number }>(`/api/sources/${id}/files/restore`, { uploadIds }),
    piiReport: (id: string) => http.get<PiiReport>(`/api/sources/${id}/pii-report`),
    preflight: (body: { collectionId: string; type: string; settings: Record<string, unknown> }) =>
      http.post<{ jobId: string }>('/api/sources/preflight', body),
  },
  uploads: {
    /** One file of a batch, raw body; `replaces` names the slot whose bytes it replaces (same document). */
    put: (batchId: string, n: number, file: File, replaces?: string) =>
      request<UploadedFile>('PUT', `/api/upload-batches/${batchId}/files/${n}`, {
        raw: file, headers: { 'X-File-Name': encodeURIComponent(file.name), ...(replaces ? { 'X-Upload-Id': replaces } : {}) },
      }),
    commit: (batchId: string, body: { files: number[]; deletes: string[] }) =>
      http.post<{ batchId: string; jobId: string; entries: number }>(`/api/upload-batches/${batchId}/commit`, body),
    discard: (batchId: string) => http.del(`/api/upload-batches/${batchId}`),
  },
  runs: {
    get: (id: string) => http.get<Run>(`/api/runs/${id}`),
    deletions: (id: string, after?: string) => http.get<DeletionProposalPage>(`/api/runs/${id}/deletions`, { after, limit: 50 }),
    deletionsCsvUrl: (id: string) => buildUrl(`/api/runs/${id}/deletions`, { format: 'csv' }),
    confirmDeletions: (id: string, proposedCount: number) => http.post<{ jobId: string }>(`/api/runs/${id}/deletions/confirm`, { proposedCount }),
    rejectDeletions: (id: string, proposedCount: number) => http.post<{ jobId: string }>(`/api/runs/${id}/deletions/reject`, { proposedCount }),
    /** Queues `cancel-run` (409 run_not_active once the run has ended); the run is `cancelled` when that job succeeds. */
    cancel: (id: string) => http.post<{ jobId: string; runId: string }>(`/api/runs/${id}/cancel`),
    items: (id: string, query: { outcome?: string; after?: number; limit?: number } = {}) =>
      http.get<KeysetPage<RunItem, number>>(`/api/runs/${id}/items`, query),
  },
  assistants: {
    list: (query: { q?: string; pageSize?: number } = {}) => http.get<Page<AssistantSummary>>('/api/assistants', query),
    get: (id: string) => http.get<Assistant>(`/api/assistants/${id}`),
    create: (body: AssistantCreate) => http.post<Assistant>('/api/assistants', body),
    draft: (id: string) => http.get<AssistantDraft>(`/api/assistants/${id}/draft`),
    saveDraft: (id: string, body: {
      gatewayModel: string | null; systemPrompt: string; collectionLinks: CollectionLink[]; querySteps: Record<string, unknown>; notFound: Record<string, unknown>
      version: string | null
    }) =>
      http.put<AssistantDraft>(`/api/assistants/${id}/draft`, body),
    update: (id: string, body: AssistantUpdate) => http.put<Assistant>(`/api/assistants/${id}`, body),
    publish: (id: string, body: { version: string; draftVersion: string; confirmBaseChanged?: boolean; confirmGeneralKnowledge?: boolean }) =>
      http.post<Assistant>(`/api/assistants/${id}/publish`, body),
    audiences: (id: string, body: { clientIds: string[]; version: string }) => http.put<Assistant>(`/api/assistants/${id}/audiences`, body),
    test: (id: string, body: { question: string; target?: 'Draft' | 'Published' }) => http.post<AssistantTestResult>(`/api/assistants/${id}/test`, body),
    versions: (id: string) => http.get<AssistantVersionSummary[]>(`/api/assistants/${id}/versions`),
    version: (id: string, number: number) => http.get<AssistantVersion>(`/api/assistants/${id}/versions/${number}`),
    restore: (id: string, number: number, version: string, confirmGeneralKnowledge = false) =>
      http.post<Assistant>(`/api/assistants/${id}/versions/${number}/restore`, { version, confirmGeneralKnowledge }),
    archive: (id: string, version: string) => http.post<Assistant>(`/api/assistants/${id}/archive`, { version }),
    unarchive: (id: string, version: string) => http.post<Assistant>(`/api/assistants/${id}/unarchive`, { version }),
    retention: (id: string, body: { historyMode: string; retentionDays: number; version: string }) => http.put<Assistant>(`/api/assistants/${id}/retention`, body),
  },
  dataProtection: {
    get: (level: string) => http.get<DataProtectionPolicy>(`/api/data-protection/${level}`),
  },
  clients: {
    list: () => http.get<ClientSummary[]>('/api/clients'),
  },
  access: {
    explain: (body: ExplainAccessRequest) => http.post<AccessExplanation>('/api/access/explain', body),
  },
  gateway: {
    /** `level`: an assistant's level, so `available` is at its ceiling; absent: the platform ceiling (Settings, V32). */
    models: (level?: string) => http.get<GatewayModel[]>(level ? `/api/gateway/models?level=${encodeURIComponent(level)}` : '/api/gateway/models'),
  },
  pipelines: {
    get: (id: string) => http.get<Pipeline>(`/api/pipelines/${id}`),
    plan: (id: string, body: { parameters: Record<string, unknown>; templateVersion?: number; collectionId?: string; version: string }) =>
      http.post<PublicationStarted>(`/api/pipelines/${id}/publications`, body),
    rollback: (id: string, body: { versionId: string; version: string }) => http.post<Pipeline>(`/api/pipelines/${id}/rollback`, body),
    list: (query: { collectionId: string; sourceType: string }) => http.get<PipelineSummary[]>('/api/pipelines', query),
    preview: (id: string, body: { collectionId: string; sourceId?: string; documentIds?: string[]; urls?: string[] }) =>
      http.post<{ jobId: string }>(`/api/pipelines/${id}/preview`, body),
  },
  publications: {
    get: (id: string) => http.get<Publication>(`/api/publications/${id}`),
    confirm: (id: string, version: string) => http.post<Publication>(`/api/publications/${id}/confirm`, { version }),
  },
  templates: {
    get: (id: string, version: number) => http.get<TemplateDetail>(`/api/templates/${id}/versions/${version}`),
  },
  previews: {
    /** `document`: the document's id or its index in the result; `stage`: a stage, or `before`/`after` of the site profile. */
    output: <T>(jobId: string, document: string | number, stage: string) =>
      http.get<PreviewOutput<T>>(`/api/previews/${jobId}/documents/${document}/stages/${stage}/output`),
  },
  evaluation: {
    questions: (collectionId: string, query: { layer?: string; state?: string; q?: string; after?: string; limit?: number } = {}) =>
      http.get<KeysetPage<EvaluationQuestion>>(`/api/collections/${collectionId}/evaluation/questions`, query),
    addQuestion: (collectionId: string, body: EvaluationQuestionInput) =>
      http.post<{ jobId: string; questionId: string }>(`/api/collections/${collectionId}/evaluation/questions`, body),
    updateQuestion: (collectionId: string, questionId: string, body: EvaluationQuestionInput) =>
      http.put<{ jobId: string; questionId: string }>(`/api/collections/${collectionId}/evaluation/questions/${questionId}`, body),
    retireQuestion: (collectionId: string, questionId: string) =>
      http.del<{ jobId: string; questionId: string }>(`/api/collections/${collectionId}/evaluation/questions/${questionId}`),
    run: (collectionId: string, body: { variants?: string[]; candidateSettings?: Record<string, unknown> } = {}) =>
      http.post<{ jobId: string; alreadyQueued: boolean }>(`/api/collections/${collectionId}/evaluation/run`, body),
    forecast: (collectionId: string) => http.get<GenerationForecast>(`/api/collections/${collectionId}/evaluation/generate`),
    generate: (collectionId: string) =>
      http.post<{ jobId: string; alreadyQueued: boolean; forecast: GenerationForecast }>(`/api/collections/${collectionId}/evaluation/generate`),
    getRun: (runId: string) => http.get<EvaluationRun>(`/api/evaluation-runs/${runId}`),
    results: (runId: string) => http.get<EvaluationResult[]>(`/api/evaluation-runs/${runId}/results`),
  },
  overview: () => http.get<Overview>('/api/overview'),
  feedback: {
    list: (query: {
      from?: string; to?: string; assistantId?: string; rating?: string; outcome?: string; state?: string; after?: string; limit?: number
    }) => http.get<KeysetPage<FeedbackItem>>('/api/feedback', query),
    summary: (query: { from?: string; to?: string; assistantId?: string } = {}) => http.get<FeedbackSummary>('/api/feedback/summary', query),
    setReviewState: (id: string, state: 'open' | 'reviewed' | 'dismissed') =>
      http.put<{ id: string; reviewState: string }>(`/api/feedback/${id}/review-state`, { state }),
  },
  audit: {
    list: (query: {
      from?: string; to?: string; actor?: string; action?: string; entityType?: string; entityId?: string; service?: string; after?: number; limit?: number
    }) => http.get<KeysetPage<AuditSummary, number>>('/api/audit', query),
    get: (id: number) => http.get<AuditDetail>(`/api/audit/${id}`),
  },
  jobs: {
    get: <T = unknown>(id: string) => http.get<Job<T>>(`/api/jobs/${id}`),
  },
  documents: {
    list: (query: { collectionId?: string; sourceId?: string; q?: string; pii?: string; after?: string; limit?: number }) =>
      http.get<KeysetPage<DocumentSummary>>('/api/documents', query),
    get: (id: string) => http.get<DocumentDetail>(`/api/documents/${id}`),
  },
}
