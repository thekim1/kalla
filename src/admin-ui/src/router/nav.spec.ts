import { describe, expect, it } from 'vitest'
import { createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import { ROLES } from '@/api/types'
import { ragRoles, useAuthStore } from '@/stores/auth'
import { createAppRouter, routes } from '.'
import { ALL_ROLES, filterEntries, mobileTabs, navGroups, searchEntries } from './nav'
import { PAGE, PLATFORM } from '@/utils/labels'

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
    expect(filterEntries(entries, 'cost centre').map((e) => e.id)).toEqual(['organisation'])
    expect(filterEntries(entries, 'permissions').map((e) => e.id)).toEqual(['overview'])
    // The knowledge bases (personal data in documents) and the Personal data settings section.
    expect(filterEntries(entries, 'personal data').map((e) => e.label)).toEqual([PLATFORM.settingsSections.pii, PAGE.collections.title])
  })

  it('finds the platform pages and the Organisation page\'s member groups and sharing by their words', () => {
    expect(filterEntries(entries, 'api keys').map((e) => e.id)).toEqual(['platform-clients'])
    expect(filterEntries(entries, 'embedding profiles').map((e) => e.id)).toContain('platform-models')
    for (const query of ['member groups', 'sharing', 'municipality']) expect(filterEntries(entries, query).map((e) => e.id)).toContain('organisation')
  })

  it('opens the old addresses of the Platform objects pages where their content went', async () => {
    setActivePinia(createPinia())
    const router = createAppRouter(createMemoryHistory())
    const auth = useAuthStore()
    auth.user = { name: 'P', email: 'p@example.se', roles: ['rag-platform-admin'] } as never
    auth.status = 'authenticated'
    for (const [from, to] of [
      ['/plattform', '/plattform/settings'], ['/plattform/keys', '/plattform/clients?tab=keys'], ['/plattform/site-profiles', '/plattform/settings/site-profiles'],
      ['/plattform/data-protection', '/plattform/settings/data-protection'], ['/plattform/organisation', '/organisation'],
    ]) {
      await router.push(from!)
      expect(router.currentRoute.value.fullPath).toBe(to)
    }
    // Each navigation loads the page's code: longer than the default 5 s under a full parallel run.
  }, 30_000)

  it('lists only pages without a query', () => {
    expect(filterEntries(entries, '').map((e) => e.id)).toEqual([
      'overview', 'map', 'collections', 'assistants', 'feedback', 'costs', 'platform-clients', 'platform-models', 'platform-settings', 'organisation', 'audit',
    ])
  })

  it('groups the menu as Home and Map, Knowledge bases and Assistants, Feedback and Costs, then the platform pages, Organisation and Audit log (admin design §5.2, §5.10)', () => {
    expect(navGroups.map((g) => g.items.map((i) => i.label))).toEqual([
      ['Home', 'Map'], ['Knowledge bases', 'Assistants'], ['Feedback', 'Costs'], ['Apps and keys', 'Models', 'Settings', 'Organisation', 'Audit log'],
    ])
    expect(filterEntries(entries, 'responsible').map((e) => e.id)).toEqual(['map'])
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

describe('Costs (admin design §3.3, §8.6; B-41)', () => {
  function signIn(roles: string[]) {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = { isAuthenticated: true, name: 'A', email: null, roles, departmentCodes: [], departmentSources: [], sessionExpiresAt: null }
    auth.status = 'authenticated'
    return auth
  }

  it('is in the menu for department and platform admins only', () => {
    const costs = navGroups.flatMap((g) => g.items).find((i) => i.name === 'costs')!
    expect(signIn(['rag-viewer']).canOpen(costs)).toBe(false)
    expect(signIn(['rag-editor']).canOpen(costs)).toBe(false)
    expect(signIn(['rag-department-admin']).canOpen(costs)).toBe(true)
    expect(signIn(['rag-platform-admin']).canOpen(costs)).toBe(true)
  })

  it('sends a viewer who opens /costs to /forbidden', async () => {
    signIn(['rag-viewer'])
    const router = createAppRouter(createMemoryHistory())
    await router.push('/costs')
    expect(router.currentRoute.value.name).toBe('forbidden')
    expect(router.currentRoute.value.query.from).toBe('/costs')
  })
})
