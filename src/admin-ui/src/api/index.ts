import { buildUrl, http, postLogout } from './client'
import type {
  BffUser,
  Collection,
  CollectionCreate,
  CollectionSummary,
  Department,
  DocumentSummary,
  KeysetPage,
  OrgSyncResult,
  Job,
  OrgSyncStatus,
  Page,
  PiiReport,
  Run,
  RunItem,
  RunSummary,
  Source,
  SourceCreate,
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
    addSource: (id: string, body: SourceCreate) => http.post<Source>(`/api/collections/${id}/sources`, body),
  },
  sources: {
    get: (id: string) => http.get<Source>(`/api/sources/${id}`),
    sync: (id: string) => http.post<SyncStarted>(`/api/sources/${id}/sync`),
    runs: (id: string, after?: string) => http.get<KeysetPage<RunSummary>>(`/api/sources/${id}/runs`, { after, limit: 20 }),
    piiReport: (id: string) => http.get<PiiReport>(`/api/sources/${id}/pii-report`),
    preflight: (body: { collectionId: string; type: string; settings: Record<string, unknown> }) =>
      http.post<{ jobId: string }>('/api/sources/preflight', body),
  },
  runs: {
    get: (id: string) => http.get<Run>(`/api/runs/${id}`),
    items: (id: string, query: { outcome?: string; after?: number; limit?: number } = {}) =>
      http.get<KeysetPage<RunItem, number>>(`/api/runs/${id}/items`, query),
  },
  jobs: {
    get: <T = unknown>(id: string) => http.get<Job<T>>(`/api/jobs/${id}`),
  },
  documents: {
    list: (query: { collectionId?: string; sourceId?: string; q?: string; pii?: string; after?: string; limit?: number }) =>
      http.get<KeysetPage<DocumentSummary>>('/api/documents', query),
  },
}
