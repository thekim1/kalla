import { buildUrl, http, postLogout, request } from './client'
import type {
  KeyBudget,
  KeyUsage,
  CostQuery,
  CostReport,
  CostReportQuery,
  AssistantCosts,
  ObjectCosts,
  CollectionLink,
  CollectionUpdate,
  AssistantUpdate,
  CollectionRead,
  CollectionView,
  DepartmentMap,
  SharingLevel,
  Topic,
  Assistant,
  AssistantCopyRequest,
  AssistantCopyResult,
  AssistantCreate,
  AssistantTemplate,
  AssistantDraft,
  AssistantSummary,
  AssistantTestResult,
  AssistantVersion,
  AssistantVersionSummary,
  VersionDiff,
  TestQuestion,
  TestQuestionRequest,
  TestRunReport,
  AnswerEvaluation,
  ContentGapReport,
  DocumentFeedbackReport,
  TestRunSummary,
  BffUser,
  OpenWebUiCompleteRequest,
  OpenWebUiSaved,
  OpenWebUiTest,
  OpenWebUiTestRequest,
  SetupCompleteRequest,
  SetupSessionStarted,
  SetupState,
  SetupTestStarted,
  SetupValuesRequest,
  ApiKey,
  Client,
  ClientConnection,
  ClientCreated,
  ClientRequest,
  ClientSummary,
  KeyCreated,
  KeyCreateRequest,
  KeyRequest,
  KeyRotated,
  KeyRotationMode,
  GatewayKeyList,
  GatewayModel,
  GeneralKnowledge,
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
  DocumentChunks,
  DocumentDetail,
  FileSort,
  FileStateFilter,
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
  MoveImpact,
  MoveRequest,
  Source,
  SourceCreate,
  DeletionProposalPage,
  HeldDocument,
  QuarantinedFile,
  UploadBatch,
  UploadedFile,
  UploadFile,
  UploadSummary,
  SourceImpact,
  SourceUpdate,
  SourceListItem,
  SourceTopicSuggestions,
  TopicRulePreview,
  PathRuleJson,
  SyncStarted,
} from './types'
import { encodeFileName } from '@/utils/uploads'

/** The admin API, one function per endpoint (adapted from the gateway's api/index.ts). */
export const api = {
  bff: {
    user: () => http.get<BffUser>('/bff/user'),
    logout: () => postLogout(),
    extendSession: () => http.post<{ sessionExpiresAt: string; sessionLimitAt: string | null }>('/bff/session/extend'),
    loginUrl: (returnUrl: string) => buildUrl('/bff/login', { returnUrl }),
  },
  /**
   * Setup mode (identity design §19, "Setup API"): 404 while setup is closed; a 401 is the setup session's, never the admin
   * session's. The code and a secret go only in the body.
   */
  setup: {
    session: (code: string) => request<SetupSessionStarted>('POST', '/bff/setup/session', { body: { code }, ownSession: true }),
    state: () => request<SetupState>('GET', '/bff/setup/state', { ownSession: true }),
    test: (body: SetupValuesRequest) => request<SetupTestStarted>('POST', '/bff/setup/test', { body, ownSession: true }),
    complete: (body: SetupCompleteRequest) => request<{ signInUrl: string }>('POST', '/bff/setup/complete', { body, ownSession: true }),
    /** Open WebUI's check: the back channel only, answered at once (no window, no polling). */
    testOpenWebUi: (body: OpenWebUiTestRequest) => request<OpenWebUiTest>('POST', '/bff/setup/test', { body, ownSession: true }),
    /** Saves Open WebUI's checked values; setup stays open. */
    completeOpenWebUi: (body: OpenWebUiCompleteRequest) => request<OpenWebUiSaved>('POST', '/bff/setup/complete', { body, ownSession: true }),
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
    /** Owning department only (404 for a department it is shared with); `under` needs `sourceId`. */
    topicSuggestions: (id: string, query: { sourceId?: string; under?: string } = {}) =>
      http.get<{ collectionId: string; sources: SourceTopicSuggestions[] }>(buildUrl(`/api/collections/${id}/topic-suggestions`, query)),
    /** EditPipelines of the owning department; an invalid rule is a 400 on `rule` (V24). Nothing is saved. */
    previewTopicRule: (id: string, body: { sourceId: string; rule: PathRuleJson }) =>
      http.post<TopicRulePreview>(`/api/collections/${id}/topic-rules/preview`, body),
    sharing: (id: string, body: { sharedWith: SharingLevel; departmentIds: string[]; version: string }) =>
      http.put<Collection>(`/api/collections/${id}/sharing`, body),
    create: (body: CollectionCreate) => http.post<Collection>('/api/collections', body),
    publish: (id: string, version: string) => http.post<Collection>(`/api/collections/${id}/publish`, { version }),
    update: (id: string, body: CollectionUpdate) => http.put<Collection>(`/api/collections/${id}`, body),
    /** #811: what moving it to another department does (platform admins). */
    moveImpact: (id: string, body: MoveRequest) => http.post<MoveImpact>(`/api/collections/${id}/move/impact`, body),
    move: (id: string, body: MoveRequest) => http.post<Collection>(`/api/collections/${id}/move`, body),
    sources: (id: string) => http.get<SourceListItem[]>(`/api/collections/${id}/sources`),
    evaluation: (id: string) => http.get<EvaluationOverview>(`/api/collections/${id}/evaluation`),
    retrievalSettings: (id: string, settings: Record<string, unknown>, version: string) =>
      http.put<Collection>(`/api/collections/${id}/retrieval-settings`, { settings, version }),
    setup: (id: string) => http.get<CollectionSetup>(`/api/collections/${id}/setup`),
    addSource: (id: string, body: SourceCreate) => http.post<Source>(`/api/collections/${id}/sources`, body),
    /** Refused with `collection_in_use` while an assistant links it or it has a source (admin design §4.5). */
    remove: (id: string, version: string) => http.del(`/api/collections/${id}?version=${encodeURIComponent(version)}`),
  },
  sources: {
    get: (id: string) => http.get<Source>(`/api/sources/${id}`),
    remove: (id: string, version: string) => http.del(`/api/sources/${id}?version=${encodeURIComponent(version)}`),
    sync: (id: string) => http.post<SyncStarted>(`/api/sources/${id}/sync`),
    /** Stop scheduled runs (admin design §5.5.3); the source as saved. */
    pause: (id: string, version: string) => http.post<Source>(`/api/sources/${id}/pause`, { version }),
    /** Run on schedule again. */
    resume: (id: string, version: string) => http.post<Source>(`/api/sources/${id}/resume`, { version }),
    retryFailed: (id: string) => http.post<SyncStarted>(`/api/sources/${id}/retry-failed`),
    impact: (id: string, body: { settings: Record<string, unknown> }) => http.post<SourceImpact>(`/api/sources/${id}/impact`, body),
    update: (id: string, body: SourceUpdate) => http.put<Source>(`/api/sources/${id}`, body),
    runs: (id: string, after?: string) => http.get<KeysetPage<RunSummary>>(`/api/sources/${id}/runs`, { after, limit: 20 }),
    held: (id: string, after?: string) => http.get<KeysetPage<HeldDocument>>(`/api/sources/${id}/held`, { after, limit: 50 }),
    releaseHeld: (id: string) => http.post<{ jobId: string }>(`/api/sources/${id}/held/release`),
    /** `q`: part of the path; `after`: the opaque cursor of the previous page's `next` (admin design §4.5, §5.5.7). */
    files: (id: string, query: { deleted?: boolean; q?: string; state?: FileStateFilter; sort?: FileSort; after?: string; limit?: number } = {}) =>
      http.get<KeysetPage<UploadFile>>(`/api/sources/${id}/files`, query),
    filesSummary: (id: string) => http.get<UploadSummary>(`/api/sources/${id}/files/summary`),
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
    /**
     * One file of a batch, raw body. `path`: its relative path (`HR/Policys/semester.pdf`, or just the name), which becomes
     * its name in the list; `replaces` names the slot whose bytes it replaces (same document).
     */
    put: (batchId: string, n: number, file: File, path: string, replaces?: string) =>
      request<UploadedFile>('PUT', `/api/upload-batches/${batchId}/files/${n}`, {
        raw: file, headers: { 'X-File-Name': encodeFileName(path), ...(replaces ? { 'X-Upload-Id': replaces } : {}) },
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
    continue: (id: string) => http.post<SyncStarted>(`/api/runs/${id}/continue`),
    items: (id: string, query: { outcome?: string; after?: number; limit?: number } = {}) =>
      http.get<KeysetPage<RunItem, number>>(`/api/runs/${id}/items`, query),
  },
  assistantTemplates: {
    /** Every role reads them: the create-assistant drawer offers the active ones (admin design §5 step 9). */
    list: () => http.get<AssistantTemplate[]>('/api/assistant-templates'),
  },
  /** An assistant's test questions and their runs (admin design §7.4, B-57). */
  assistantTests: {
    questions: (id: string) => http.get<TestQuestion[]>(`/api/assistants/${id}/test-questions`),
    create: (id: string, body: TestQuestionRequest) => http.post<TestQuestion>(`/api/assistants/${id}/test-questions`, body),
    update: (id: string, qid: string, body: TestQuestionRequest) => http.put<TestQuestion>(`/api/assistants/${id}/test-questions/${qid}`, body),
    remove: (id: string, qid: string, version: string) => http.del(`/api/assistants/${id}/test-questions/${qid}?version=${encodeURIComponent(version)}`),
    run: (id: string, target: 'Draft' | 'Published') => http.post<TestRunSummary>(`/api/assistants/${id}/test-runs`, { target }),
    runs: (id: string) => http.get<TestRunSummary[]>(`/api/assistants/${id}/test-runs`),
    report: (id: string, runId: string) => http.get<TestRunReport>(`/api/assistants/${id}/test-runs/${runId}`),
    /** The latest nightly answer evaluation beside the live uncited rate (B-58). */
    answerEvaluation: (id: string) => http.get<AnswerEvaluation>(`/api/assistants/${id}/answer-evaluation`),
  },
  assistants: {
    list: (query: { q?: string; page?: number; pageSize?: number } = {}) => http.get<Page<AssistantSummary>>('/api/assistants', query),
    get: (id: string) => http.get<Assistant>(`/api/assistants/${id}`),
    /** From any state (Decisions "Deleting knowledge bases, sources and assistants"): its name is free at once. */
    remove: (id: string, version: string) => http.del(`/api/assistants/${id}?version=${encodeURIComponent(version)}`),
    create: (body: AssistantCreate) => http.post<Assistant>('/api/assistants', body),
    draft: (id: string) => http.get<AssistantDraft>(`/api/assistants/${id}/draft`),
    saveDraft: (id: string, body: {
      gatewayModel: string | null; systemPrompt: string; collectionLinks: CollectionLink[]; querySteps: Record<string, unknown>; notFound: Record<string, unknown>
      version: string | null
    }) =>
      http.put<AssistantDraft>(`/api/assistants/${id}/draft`, body),
    update: (id: string, body: AssistantUpdate) => http.put<Assistant>(`/api/assistants/${id}`, body),
    /** #811: what moving it to another department does (platform admins). */
    moveImpact: (id: string, body: MoveRequest) => http.post<MoveImpact>(`/api/assistants/${id}/move/impact`, body),
    move: (id: string, body: MoveRequest) => http.post<Assistant>(`/api/assistants/${id}/move`, body),
    publish: (id: string, body: { version: string; draftVersion: string; confirmBaseChanged?: boolean; confirmGeneralKnowledge?: boolean }) =>
      http.post<Assistant>(`/api/assistants/${id}/publish`, body),
    audiences: (id: string, body: { clientIds: string[]; version: string }) => http.put<Assistant>(`/api/assistants/${id}/audiences`, body),
    test: (id: string, body: { question: string; target?: 'Draft' | 'Published'; generalKnowledge?: GeneralKnowledge }) =>
      http.post<AssistantTestResult>(`/api/assistants/${id}/test`, body),
    versions: (id: string) => http.get<AssistantVersionSummary[]>(`/api/assistants/${id}/versions`),
    version: (id: string, number: number) => http.get<AssistantVersion>(`/api/assistants/${id}/versions/${number}`),
    /** What changed from version `number` to `against` (a number or `draft`; B-56). */
    diff: (id: string, number: number, against: number | 'draft') =>
      http.get<VersionDiff>(`/api/assistants/${id}/versions/${number}/diff`, { against: String(against) }),
    restore: (id: string, number: number, version: string, confirmGeneralKnowledge = false) =>
      http.post<Assistant>(`/api/assistants/${id}/versions/${number}/restore`, { version, confirmGeneralKnowledge }),
    /** A new assistant with a draft of this one's published version (B-55). */
    copy: (id: string, body: AssistantCopyRequest) => http.post<AssistantCopyResult>(`/api/assistants/${id}/copy`, body),
    archive: (id: string, version: string) => http.post<Assistant>(`/api/assistants/${id}/archive`, { version }),
    unarchive: (id: string, version: string) => http.post<Assistant>(`/api/assistants/${id}/unarchive`, { version }),
    retention: (id: string, body: { historyMode: string; retentionDays: number; version: string }) => http.put<Assistant>(`/api/assistants/${id}/retention`, body),
  },
  dataProtection: {
    get: (level: string) => http.get<DataProtectionPolicy>(`/api/data-protection/${level}`),
  },
  clients: {
    list: () => http.get<ClientSummary[]>('/api/clients'),
    get: (id: string) => http.get<Client>(`/api/clients/${id}`),
    /** `firstKey` and `assistantIds`: a key and the assistants it offers, made with the client in one transaction (admin design §5.9, §5.9.1). */
    create: (body: ClientRequest & { firstKey: { name: string; teamId?: string | null } | null; assistantIds?: string[] }) =>
      http.post<ClientCreated>('/api/clients', body),
    update: (id: string, body: ClientRequest) => http.put<Client>(`/api/clients/${id}`, body),
    /** The assistants that offer the client, set from its side; `version` is the client's. */
    setAssistants: (id: string, body: { assistantIds: string[]; version: string }) => http.put<Client>(`/api/clients/${id}/assistants`, body),
    connection: () => http.get<ClientConnection>('/api/clients/connection'),
  },
  keys: {
    list: (clientId?: string) => http.get<ApiKey[]>('/api/keys', { clientId }),
    create: (body: KeyCreateRequest) => http.post<KeyCreated>('/api/keys', body),
    update: (id: string, body: KeyRequest) => http.put<ApiKey>(`/api/keys/${id}`, body),
    rotate: (id: string, mode: KeyRotationMode) => http.post<KeyRotated>(`/api/keys/${id}/rotate`, { mode }),
    /** Platform admins; every reveal is audited (identity §6.4). */
    reveal: (id: string) => http.post<{ secret: string }>(`/api/keys/${id}/reveal`, { purpose: 'Reveal' }),
    revoke: (id: string) => http.post<ApiKey>(`/api/keys/${id}/revoke`),
  },
  access: {
    explain: (body: ExplainAccessRequest) => http.post<AccessExplanation>('/api/access/explain', body),
  },
  gateway: {
    /** `level`: an assistant's level, so `available` is at its ceiling; absent: the platform ceiling (Settings, V32). */
    /** `gatewayKeyId` (B-43): once the gateway acts as keys, `available` is what that key may use. */
    models: (level?: string, gatewayKeyId?: string | null) =>
      http.get<GatewayModel[]>('/api/gateway/models', { ...(level ? { level } : {}), ...(gatewayKeyId ? { gatewayKeyId } : {}) }),
    /** The department's assigned gateway keys for the key picker (B-36). */
    keys: (departmentId: string) => http.get<GatewayKeyList>('/api/gateway/keys', { departmentId }),
    /** A key's budgets from the gateway's Integration API (B-45); null without the read-back. */
    keyBudget: (id: string) => http.get<KeyBudget[] | null>(`/api/gateway/keys/${id}/budget`),
    /** A key's usage per period and step as the gateway counted it (B-45); null without the read-back. */
    keyUsage: (id: string, query: CostQuery = {}) => http.get<KeyUsage | null>(`/api/gateway/keys/${id}/usage`, { ...query }),
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
  costs: {
    assistant: (id: string, query: CostQuery = {}) => http.get<AssistantCosts>(`/api/assistants/${id}/costs`, { ...query }),
    collection: (id: string, query: CostQuery = {}) => http.get<ObjectCosts>(`/api/collections/${id}/costs`, { ...query }),
    report: (query: CostReportQuery = {}) => http.get<CostReport>('/api/costs', { ...query }),
    /** The report's CSV with the same filters (admin design §8.6). */
    csvUrl: (query: CostReportQuery = {}) => buildUrl('/api/costs', { ...query, format: 'csv' }),
  },
  gaps: {
    /** An assistant's content gaps of a period (admin design §8.7). */
    assistant: (id: string, query: { from?: string; to?: string } = {}) => http.get<ContentGapReport>(`/api/assistants/${id}/gaps`, { ...query }),
    /** The same report as CSV. */
    csvUrl: (id: string, query: { from?: string; to?: string } = {}) => buildUrl(`/api/assistants/${id}/gaps`, { ...query, format: 'csv' }),
  },
  documentFeedback: {
    /** A knowledge base's documents by the ratings of the answers that cited them (admin design §8.8). */
    collection: (id: string, query: { from?: string; to?: string } = {}) =>
      http.get<DocumentFeedbackReport>(`/api/collections/${id}/feedback`, { ...query }),
    /** The same report as CSV. */
    csvUrl: (id: string, query: { from?: string; to?: string } = {}) => buildUrl(`/api/collections/${id}/feedback`, { ...query, format: 'csv' }),
  },
  feedback: {
    list: (query: {
      from?: string; to?: string; assistantId?: string; rating?: string; outcome?: string; hasComment?: boolean; state?: string; documentId?: string
      after?: string; limit?: number
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
    chunks: (id: string) => http.get<DocumentChunks>(`/api/documents/${id}/chunks`),
    /** The personal-data highlights (a preview up to the personal-data stage, audited pii.view; B-63). */
    piiPreview: (id: string) => http.post<{ jobId: string }>(`/api/documents/${id}/pii-preview`),
    /** Releases a flagged document (reason ≥ 10, V26; command clear-pii-flag). */
    piiClear: (id: string, reason: string) => http.post<{ jobId: string }>(`/api/documents/${id}/pii-clear`, { reason }),
  },
}
