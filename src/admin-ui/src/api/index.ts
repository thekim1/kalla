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
  OrgSyncStatus,
  Page,
  SourceSummary,
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
  },
  documents: {
    list: (query: { collectionId?: string; sourceId?: string; q?: string; pii?: string; after?: string; limit?: number }) =>
      http.get<KeysetPage<DocumentSummary>>('/api/documents', query),
  },
}
