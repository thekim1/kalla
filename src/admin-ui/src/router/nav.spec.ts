import { describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { ROLES } from '@/api/types'
import { ragRoles, useAuthStore } from '@/stores/auth'
import { routes } from '.'
import { ALL_ROLES, filterEntries, mobileTabs, navGroups, searchEntries } from './nav'

describe('menu structure (copied checks)', () => {
  const items = navGroups.flatMap((g) => g.items)

  it('lists every page once, and every menu page exists as a route', () => {
    const names = items.map((i) => i.name)
    expect(new Set(names).size).toBe(names.length)
    for (const name of names) expect(routes.some((r) => r.name === name)).toBe(true)
    for (const tab of mobileTabs) expect(names).toContain(tab.name)
  })

  it('uses the page title as the menu label, so the menu and the page agree', () => {
    for (const item of items) expect(routes.find((r) => r.name === item.name)?.meta?.title).toBe(item.label)
  })

  it('gives every page the same roles in the menu and as a route', () => {
    for (const item of items) expect(routes.find((r) => r.name === item.name)?.meta?.roles).toEqual(item.roles)
  })
})

describe('search', () => {
  const entries = searchEntries(navGroups)

  it('finds pages by words people use that are not in the label', () => {
    expect(filterEntries(entries, 'kostnadsställe').map((e) => e.id)).toEqual(['organisation'])
    expect(filterEntries(entries, 'behörighet').map((e) => e.id)).toEqual(['overview'])
    expect(filterEntries(entries, 'personuppgifter').map((e) => e.id)).toEqual(['collections'])
  })

  it('lists only pages without a query', () => {
    expect(filterEntries(entries, '').map((e) => e.id)).toEqual(['overview', 'collections', 'organisation', 'platform'])
  })
})

describe('roles', () => {
  it('counts only rag-* roles', () => {
    expect([...ragRoles(['rag-viewer', 'gateway-admin', 'offline_access'])]).toEqual(['rag-viewer'])
    expect(ALL_ROLES).toEqual([...ROLES])
  })

  it('opens the pages to any rag-* role and nothing to a user without one', () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = { isAuthenticated: true, name: 'A', email: null, roles: ['rag-viewer'], departmentCodes: [], departmentSources: [], sessionExpiresAt: null }
    auth.status = 'authenticated'
    expect(auth.canOpen({ roles: ALL_ROLES })).toBe(true)
    expect(auth.canOpen({ roles: ['rag-platform-admin'] })).toBe(false)
    auth.user = { ...auth.user, roles: ['some-other-app-role'] }
    expect(auth.hasAccess).toBe(false)
    expect(auth.canOpen({ roles: ALL_ROLES })).toBe(false)
    // The no-access page itself stays open, or the guard would redirect forever.
    expect(auth.canOpen(routes.find((r) => r.name === 'forbidden')!.meta!)).toBe(true)
  })
})
