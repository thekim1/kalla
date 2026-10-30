import { buildUrl, http, postLogout } from './client'
import type { BffUser, Department, OrgSyncResult, OrgSyncStatus } from './types'

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
}
