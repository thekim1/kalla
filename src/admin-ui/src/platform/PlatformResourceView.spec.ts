import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { filterEntries, navGroups, searchEntries } from '@/router/nav'
import { PLATFORM } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const departments = [
  {
    id: 'd1', name: 'Kommunledningskontoret', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T12:00:00Z',
    memberGroups: ['Kommunstyrelsen', 'KLK-alla'], teams: [],
    sharing: { department: 2, selected: 1, municipality: 3, sharedWithDepartments: 4 },
  },
]

const openApi = {
  paths: {
    '/api/organisation/departments/{id}/member-groups': {
      put: { requestBody: { content: { 'application/json': { schema: { $ref: '#/components/schemas/MemberGroupsRequest' } } } } },
    },
  },
  components: { schemas: { MemberGroupsRequest: { properties: { memberGroups: { type: 'array', items: { type: 'string' } } } } } },
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
})

describe('Platform objects: organisation (admin design §5.8)', () => {
  it('lists each department with its member groups and sharing summary, and opens its member groups with the sharing per level', async () => {
    setActivePinia(createPinia())
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? openApi : departments))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'organisation' }, attachTo: document.body })
    await flushPromises()

    const row = wrapper.get('tbody tr')
    expect(row.text()).toContain('Kommunledningskontoret')
    expect(row.text()).toContain('Kommunstyrelsen, KLK-alla')
    expect(row.text()).toContain(PLATFORM.organisation.summary(2, 1, 3))

    await row.trigger('click')
    await flushPromises()
    const details = wrapper.get('[data-test="details"]')
    expect(details.text()).toContain(PLATFORM.organisation.sharing)
    const facts = details.findAll('dt').map((dt, i) => [dt.text(), details.findAll('dd')[i]!.text()])
    expect(facts).toEqual([
      [PLATFORM.organisation.department, '2'],
      [PLATFORM.organisation.selected, '1'],
      [PLATFORM.organisation.sharedWith, '4'],
      [PLATFORM.organisation.municipality, '3'],
    ])
    expect((wrapper.get('#platform-organisation-memberGroups').element as HTMLTextAreaElement).value).toBe('Kommunstyrelsen\nKLK-alla')
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.memberGroups)
    wrapper.unmount()
  })

  it('shows a row\'s actions above its edit form', async () => {
    setActivePinia(createPinia())
    const keys = [{ id: 'k1', name: 'Open WebUI', prefix: 'rag_ab', status: 'active', assistantIds: [], collectionIds: [] }]
    const doc = { paths: { '/api/keys/{id}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { name: { type: 'string' } } } } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? doc : keys))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'keys' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test="action-rotate"]').exists()).toBe(true)
    expect(wrapper.find('[data-test="details"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows the per-key chat limit hint under a key\'s requests per minute (chat design §4.1 step 4)', async () => {
    setActivePinia(createPinia())
    const keys = [{ id: 'k1', name: 'Support app', prefix: 'rag_ab', status: 'active', assistantIds: [], collectionIds: [], requestsPerMinute: 60 }]
    const schema = { properties: { name: { type: 'string' }, requestsPerMinute: { type: ['null', 'integer'], format: 'int32', maximum: 100000, minimum: 1 } } }
    const doc = { paths: { '/api/keys/{id}': { put: { requestBody: { content: { 'application/json': { schema } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? doc : keys))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'keys' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect((wrapper.get('#platform-keys-requestsPerMinute').element as HTMLInputElement).value).toBe('60')
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.requestsPerMinute)
    wrapper.unmount()
  })

  it('is found in search by English words', () => {
    const entries = searchEntries(navGroups)
    for (const query of ['member groups', 'sharing', 'municipality']) {
      expect(filterEntries(entries, query).map((e) => e.label)).toContain(PLATFORM.resources.organisation.label)
    }
  })
})
