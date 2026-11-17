import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api } from '@/api'
import { readCookie, XSRF_COOKIE } from '@/api/client'
import { ROLES, type BffUser, type Role } from '@/api/types'

export type AuthStatus = 'unknown' | 'loading' | 'authenticated' | 'unauthenticated' | 'error'

/** The rag-* roles of `roles`; other values grant nothing (identity design §1.3). Roles add up, as the API's policies do. */
export function ragRoles(roles: readonly string[]): Set<Role> {
  return new Set(roles.filter((r): r is Role => (ROLES as readonly string[]).includes(r)))
}

export const useAuthStore = defineStore('auth', () => {
  const user = ref<BffUser | null>(null)
  const status = ref<AuthStatus>('unknown')
  let pending: Promise<void> | null = null

  const isAuthenticated = computed(() => status.value === 'authenticated' && !!user.value?.isAuthenticated)
  const roles = computed<string[]>(() => (isAuthenticated.value ? (user.value?.roles ?? []) : []))
  const effective = computed(() => ragRoles(roles.value))
  const isDevelopment = computed(() => user.value?.environment === 'Development')
  const departmentCodes = computed(() => user.value?.departmentCodes ?? [])
  const departmentSources = computed(() => user.value?.departmentSources ?? [])
  const sessionExpiresAt = computed<Date | null>(() => toDate(user.value?.sessionExpiresAt))
  const sessionLimitAt = computed<Date | null>(() => toDate(user.value?.sessionLimitAt))
  /** Whether the deployment runs Open WebUI; until the user document says otherwise, it does. */
  const openWebUiEnabled = computed(() => user.value?.openWebUi.enabled !== false)

  function toDate(value: string | null | undefined): Date | null {
    if (!value) return null
    const date = new Date(value)
    return Number.isNaN(date.getTime()) ? null : date
  }

  function hasRole(role: Role): boolean {
    return effective.value.has(role)
  }

  function hasAnyRole(required: readonly Role[] | undefined): boolean {
    if (!required || required.length === 0) return isAuthenticated.value
    return required.some((r) => hasRole(r))
  }

  const isPlatformAdmin = computed(() => hasRole('rag-platform-admin'))
  /** Any rag-* role; without one every /api call is 403 and the UI shows the no-access page (admin design §5.6). */
  const hasAccess = computed(() => effective.value.size > 0)

  /**
   * Whether a page is open to the user: any of its roles (the API decides; this only hides what it would refuse). A page
   * without roles (no access, not found) is open to every signed-in user, also one without a rag-* role.
   */
  function canOpen(page: { roles?: readonly Role[] }): boolean {
    return page.roles?.length ? hasAccess.value && hasAnyRole(page.roles) : isAuthenticated.value
  }

  /** Platform admins manage every department; department admins those in their scope (claim and member groups). */
  function canManageDepartment(costCenterCode: string | null | undefined): boolean {
    if (isPlatformAdmin.value) return true
    if (!hasRole('rag-department-admin') || !costCenterCode) return false
    return departmentCodes.value.includes(costCenterCode)
  }

  async function load(): Promise<void> {
    status.value = 'loading'
    try {
      const result = await api.bff.user()
      user.value = result
      status.value = result?.isAuthenticated ? 'authenticated' : 'unauthenticated'
    } catch {
      user.value = null
      status.value = 'error'
    }
  }

  function ensureLoaded(): Promise<void> {
    if (status.value === 'authenticated' || status.value === 'unauthenticated') return Promise.resolve()
    if (!pending) {
      pending = load().finally(() => {
        pending = null
      })
    }
    return pending
  }

  function setUnauthenticated(): void {
    status.value = 'unauthenticated'
    if (user.value) user.value = { ...user.value, isAuthenticated: false }
  }

  /** Explicit, CSRF-protected renewal preserves unsaved forms in this tab. */
  async function extendSession(): Promise<boolean> {
    const result = await api.bff.extendSession()
    if (user.value) user.value = { ...user.value, sessionExpiresAt: result.sessionExpiresAt, sessionLimitAt: result.sessionLimitAt }
    return isAuthenticated.value
  }

  function loginUrl(returnUrl: string): string {
    const safe = returnUrl.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/'
    return api.bff.loginUrl(safe)
  }

  async function logout(): Promise<string> {
    const target = await api.bff.logout()
    user.value = null
    status.value = 'unauthenticated'
    return target
  }

  const csrfToken = (): string | null => readCookie(XSRF_COOKIE)

  return {
    user,
    status,
    isAuthenticated,
    roles,
    departmentCodes,
    departmentSources,
    sessionExpiresAt,
    sessionLimitAt,
    openWebUiEnabled,
    isDevelopment,
    isPlatformAdmin,
    hasAccess,
    canOpen,
    hasRole,
    hasAnyRole,
    canManageDepartment,
    load,
    ensureLoaded,
    setUnauthenticated,
    extendSession,
    loginUrl,
    logout,
    csrfToken,
  }
})
