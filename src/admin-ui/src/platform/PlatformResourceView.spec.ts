import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { filterEntries, navGroups, searchEntries } from '@/router/nav'
import { PLATFORM } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { platformEntries } from './resources'
import { resetOpenApiCache } from './schema'

const departments = [
  {
    id: 'd1', name: 'Kommunledningskontoret', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T12:00:00Z',
    memberGroups: ['Kommunstyrelsen', 'KLK-alla'], teams: [],
    sharing: { department: 2, selected: 1, municipality: 3, sharedWithDepartments: 4 },
  },
]

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
})

describe('Platform objects: organisation (admin design §5.8)', () => {
  function mountOrganisation(syncStatus: unknown) {
    setActivePinia(createPinia())
    const fetch = vi.fn<(url: string, init?: RequestInit) => Promise<Response>>((url) => Promise.resolve(json(url === '/api/organisation/sync' ? syncStatus : departments)))
    vi.stubGlobal('fetch', fetch)
    return { fetch, wrapper: mount(PlatformResourceView, { props: { resource: 'organisation' }, attachTo: document.body }) }
  }

  it('lists each department with its member groups and sharing summary, and opens them read-only with the sharing per level', async () => {
    const { fetch, wrapper } = mountOrganisation({ gateway: null })
    await flushPromises()

    const row = wrapper.get('tbody tr')
    expect(row.text()).toContain('Kommunledningskontoret')
    expect(row.text()).toContain('Kommunstyrelsen, KLK-alla')
    expect(row.text()).toContain(PLATFORM.organisation.summary(2, 1, 3))

    await row.trigger('click')
    await flushPromises()
    const [groups, sharing] = wrapper.findAll('[data-test="details"]')
    expect(groups!.get('h3').text()).toBe(PLATFORM.organisation.memberGroups)
    expect(groups!.findAll('li').map((li) => li.text())).toEqual(['Kommunstyrelsen', 'KLK-alla'])
    expect(sharing!.text()).toContain(PLATFORM.organisation.sharing)
    const facts = sharing!.findAll('dt').map((dt, i) => [dt.text(), sharing!.findAll('dd')[i]!.text()])
    expect(facts).toEqual([
      [PLATFORM.organisation.department, '2'],
      [PLATFORM.organisation.selected, '1'],
      [PLATFORM.organisation.sharedWith, '4'],
      [PLATFORM.organisation.municipality, '3'],
    ])

    // No edit: no form, no save, and nothing but GETs (the member-groups PUT is gone, B-35 part 2).
    expect(document.body.querySelector('form')).toBeNull()
    expect(document.body.textContent).not.toContain(PLATFORM.form.save)
    expect(fetch.mock.calls.every(([, init]) => (init?.method ?? 'GET') === 'GET')).toBe(true)
    expect(fetch.mock.calls.map(([url]) => String(url)).some((url) => url.includes('member-groups'))).toBe(false)
    wrapper.unmount()
  })

  it('links the gateway\'s Organisation page in a new tab when the sync knows the gateway\'s admin URL (admin design §2.3)', async () => {
    const { wrapper } = mountOrganisation({
      gateway: { version: '0.10.0', adminUrl: 'https://gateway-admin.example.invalid/', features: [] },
      adminUrl: 'https://gateway-admin.example.invalid/',
    })
    await flushPromises()

    const link = wrapper.get('[data-test="gateway-page"]')
    expect(link.element.tagName).toBe('A')
    expect(link.attributes('href')).toBe('https://gateway-admin.example.invalid/organisation')
    expect(link.attributes('target')).toBe('_blank')
    expect(link.attributes('rel')).toContain('noopener')
    expect(link.text()).toContain(PLATFORM.organisation.gatewayLink)
    wrapper.unmount()
  })

  it('links the configured fallback when the gateway\'s context has no admin URL (Admin:GatewayAdminUrl, #635)', async () => {
    const { wrapper } = mountOrganisation({
      gateway: { version: '0.10.0', adminUrl: null, features: [] },
      adminUrl: 'https://gateway-fallback.example.invalid',
    })
    await flushPromises()

    expect(wrapper.get('[data-test="gateway-page"]').attributes('href')).toBe('https://gateway-fallback.example.invalid/organisation')
    wrapper.unmount()
  })

  it('shows no gateway link when the admin URL is unknown or not an http(s) address, or the sync status cannot be read', async () => {
    const notALink = { gateway: { version: '0.10.0', adminUrl: 'javascript:alert(1)', features: [] }, adminUrl: 'javascript:alert(1)' }
    // A context's admin URL alone no longer links: the status's adminUrl is the one the links use.
    const contextOnly = { gateway: { version: '0.10.0', adminUrl: 'https://gateway-admin.example.invalid/', features: [] }, adminUrl: null }
    for (const status of [{ gateway: { version: '0.10.0', adminUrl: null, features: [] }, adminUrl: null }, { gateway: null, adminUrl: null }, notALink, contextOnly]) {
      const { wrapper } = mountOrganisation(status)
      await flushPromises()
      expect(wrapper.find('tbody tr').exists()).toBe(true)
      expect(wrapper.find('[data-test="gateway-page"]').exists()).toBe(false)
      wrapper.unmount()
    }

    setActivePinia(createPinia())
    vi.stubGlobal('fetch', vi.fn((url: string) =>
      Promise.resolve(url === '/api/organisation/sync' ? new Response(null, { status: 500 }) : json(departments))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'organisation' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('tbody tr').exists()).toBe(true)
    expect(wrapper.find('[data-test="gateway-page"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows a row\'s actions above its edit form', async () => {
    setActivePinia(createPinia())
    const profiles = [{ id: 'sp1', host: 'www.umea.se', presetId: 'sitevision', appliedVersion: 2, appliedAt: '2026-11-01T12:00:00Z' }]
    const doc = { paths: { '/api/site-profiles/{id}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { presetId: { type: 'string' } } } } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(
      url === '/openapi/v1.json' ? doc : url === '/api/site-profiles' ? profiles : { profile: {} },
    ))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'site-profiles' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test="action-rollback"]')).not.toBeNull()
    expect(document.body.querySelector('[data-test="row-id"]')!.textContent).toContain('sp1')
    expect(wrapper.find('[data-test="details"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('has no generic Keys page: keys are under Apps and keys (admin design §5.9.1)', () => {
    expect(platformEntries.map((e) => e.id)).not.toContain('keys')
    expect(filterEntries(searchEntries(navGroups), 'api keys').map((e) => e.label)).toContain(PLATFORM.resources.clients.label)
  })

  it('is found in search by English words', () => {
    const entries = searchEntries(navGroups)
    for (const query of ['member groups', 'sharing', 'municipality']) {
      expect(filterEntries(entries, query).map((e) => e.label)).toContain(PLATFORM.resources.organisation.label)
    }
  })
})
