import { buildUrl, http, postLogout, request } from './client'
import type {
  Assistant,
  AssistantCreate,
  AssistantDraft,
  AssistantSummary,
  AssistantTestResult,
  AssistantVersionSummary,
  BffUser,
  ClientSummary,
  GatewayModel,
  Collection,
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
  QuarantinedFile,
  UploadBatch,
  UploadedFile,
  UploadFile,
  SourceImpact,
  SourceUpdate,
  SourceSummary,
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
  },
  collections: {
    list: (query: { q?: string; page?: number; pageSize?: number } = {}) => http.get<Page<CollectionSummary>>('/api/collections', query),
    get: (id: string) => http.get<Collection>(`/api/collections/${id}`),
    create: (body: CollectionCreate) => http.post<Collection>('/api/collections', body),
    publish: (id: string, version: string) => http.post<Collection>(`/api/collections/${id}/publish`, { version }),
    sources: (id: string) => http.get<SourceSummary[]>(`/api/collections/${id}/sources`),
    evaluation: (id: string) => http.get<EvaluationOverview>(`/api/collections/${id}/evaluation`),
    addSource: (id: string, body: SourceCreate) => http.post<Source>(`/api/collections/${id}/sources`, body),
  },
  sources: {
    get: (id: string) => http.get<Source>(`/api/sources/${id}`),
    sync: (id: string) => http.post<SyncStarted>(`/api/sources/${id}/sync`),
    impact: (id: string, body: { settings: Record<string, unknown> }) => http.post<SourceImpact>(`/api/sources/${id}/impact`, body),
    update: (id: string, body: SourceUpdate) => http.put<Source>(`/api/sources/${id}`, body),
    runs: (id: string, after?: string) => http.get<KeysetPage<RunSummary>>(`/api/sources/${id}/runs`, { after, limit: 20 }),
    files: (id: string, query: { deleted?: boolean; after?: string; limit?: number } = {}) =>
      http.get<KeysetPage<UploadFile>>(`/api/sources/${id}/files`, query),
    quarantine: (id: string, after?: string) => http.get<KeysetPage<QuarantinedFile>>(`/api/sources/${id}/quarantine`, { after, limit: 50 }),
    uploadBatch: (id: string) => http.post<UploadBatch>(`/api/sources/${id}/upload-batches`),
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
    items: (id: string, query: { outcome?: string; after?: number; limit?: number } = {}) =>
      http.get<KeysetPage<RunItem, number>>(`/api/runs/${id}/items`, query),
  },
  assistants: {
    list: (query: { q?: string; pageSize?: number } = {}) => http.get<Page<AssistantSummary>>('/api/assistants', query),
    get: (id: string) => http.get<Assistant>(`/api/assistants/${id}`),
    create: (body: AssistantCreate) => http.post<Assistant>('/api/assistants', body),
    draft: (id: string) => http.get<AssistantDraft>(`/api/assistants/${id}/draft`),
    saveDraft: (id: string, body: { gatewayModel: string | null; systemPrompt: string; collectionIds: string[]; version: string | null }) =>
      http.put<AssistantDraft>(`/api/assistants/${id}/draft`, body),
    publish: (id: string, body: { version: string; draftVersion: string; confirmBaseChanged?: boolean }) => http.post<Assistant>(`/api/assistants/${id}/publish`, body),
    audiences: (id: string, body: { clientIds: string[]; version: string }) => http.put<Assistant>(`/api/assistants/${id}/audiences`, body),
    test: (id: string, body: { question: string; target?: 'Draft' | 'Published' }) => http.post<AssistantTestResult>(`/api/assistants/${id}/test`, body),
    versions: (id: string) => http.get<AssistantVersionSummary[]>(`/api/assistants/${id}/versions`),
    restore: (id: string, number: number, version: string) => http.post<Assistant>(`/api/assistants/${id}/versions/${number}/restore`, { version }),
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
  gateway: {
    models: () => http.get<GatewayModel[]>('/api/gateway/models'),
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
  jobs: {
    get: <T = unknown>(id: string) => http.get<Job<T>>(`/api/jobs/${id}`),
  },
  documents: {
    list: (query: { collectionId?: string; sourceId?: string; q?: string; pii?: string; after?: string; limit?: number }) =>
      http.get<KeysetPage<DocumentSummary>>('/api/documents', query),
  },
}
