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

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
})

/** The Keys page's calls: the document, the keys, and the key form's pickers. */
function keysFetch(doc: unknown, keys: unknown[], clients: unknown[] = []) {
  return vi.fn<(url: string, init?: RequestInit) => Promise<Response>>((url) => {
    if (url === '/openapi/v1.json') return Promise.resolve(json(doc))
    if (url === '/api/keys') return Promise.resolve(json(keys))
    if (url === '/api/clients') return Promise.resolve(json(clients))
    if (url.startsWith('/api/assistants') || url.startsWith('/api/collections')) return Promise.resolve(json({ items: [], total: 0 }))
    return Promise.resolve(json([]))
  })
}

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
    const keys = [{ id: 'k1', name: 'Open WebUI', prefix: 'rag_ab', status: 'active', assistantIds: [], collectionIds: [] }]
    const doc = { paths: { '/api/keys/{id}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { name: { type: 'string' } } } } } } } } } }
    vi.stubGlobal('fetch', keysFetch(doc, keys))
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
    vi.stubGlobal('fetch', keysFetch(doc, keys))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'keys' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect((wrapper.get('#platform-keys-requestsPerMinute').element as HTMLInputElement).value).toBe('60')
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.requestsPerMinute)
    wrapper.unmount()
  })

  it('lists keys with their client, edits a key with the key form and shows the row\'s id with a copy button (admin design §5.8, §5.9)', async () => {
    setActivePinia(createPinia())
    const keys = [{ id: 'k1', clientId: 'c1', clientName: 'Open WebUI', name: 'Pilot', prefix: 'rag_ab', status: 'active', assistantIds: [], collectionIds: [], allowedIpRanges: [] }]
    const scopes = { type: 'object', properties: { assistantIds: { type: 'array', items: { type: 'string' } } } }
    const doc = { paths: { '/api/keys/{id}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { name: { type: 'string' }, scopes } } } } } } } } }
    vi.stubGlobal('fetch', keysFetch(doc, keys))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'keys' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.findAll('thead th').map((th) => th.text())).toContain(PLATFORM.columns.client)
    expect(wrapper.get('tbody tr').text()).toContain('Open WebUI')

    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=row-id]')!.textContent).toContain('k1')
    expect(document.body.querySelector('[data-test=key-form]')).not.toBeNull()
    expect(document.body.querySelector('#platform-keys-scopes')!.tagName).toBe('FIELDSET')
    expect(document.body.querySelector('textarea#platform-keys-scopes')).toBeNull()
    expect(document.body.querySelector<HTMLInputElement>('#platform-keys-name')!.value).toBe('Pilot')
    wrapper.unmount()
  })

  it('makes a new key on the Keys page from a client picked first (admin design §5.9)', async () => {
    setActivePinia(createPinia())
    const doc = { paths: { '/api/keys': { post: { requestBody: { content: { 'application/json': { schema: { properties: { name: { type: 'string' } } } } } } } } } }
    const fetch = keysFetch(doc, [], [{ id: 'c1', name: 'Open WebUI', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, activeKeys: 0 }])
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(PlatformResourceView, { props: { resource: 'keys' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-test=create]').trigger('click')
    await flushPromises()
    const select = document.body.querySelector<HTMLSelectElement>('select#platform-keys-clientId')!
    expect([...select.options].map((o) => o.text)).toContain('Open WebUI')
    expect(document.body.querySelector('[data-test=row-id]')).toBeNull()
    wrapper.unmount()
  })

  it('edits the crawl section with the host politeness table instead of its JSON (admin design §5.8)', async () => {
    setActivePinia(createPinia())
    const crawl = { version: '3', value: { allowedPorts: [8443], hostOverrides: { 'www.umea.se': { preset: 'fast' } } } }
    const doc = { paths: { '/api/settings/{section}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { value: { type: 'object' } } } } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? doc : crawl))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'settings' }, attachTo: document.body })
    await flushPromises()

    const row = wrapper.findAll('tbody tr').find((r) => r.text().includes(PLATFORM.settingsSections.crawl))!
    await row.trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=crawl-settings]')).not.toBeNull()
    expect(document.body.querySelector('[data-test=host-politeness]')!.textContent).toContain('www.umea.se')
    expect(document.body.querySelector('#platform-settings-value')).toBeNull()
    wrapper.unmount()
  })

  it('gives the evaluation section\'s model fields a picker over the mirror at the platform ceiling (#466)', async () => {
    setActivePinia(createPinia())
    const evaluation = { evaluationModel: 'gpt-oss', evaluationJudgeModel: null, evaluationMaxQuestions: 100, version: '2' }
    const models = [
      { name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['onprem'], available: true },
      { name: 'rerank-1', kind: 'model', modelKind: 'rerank', residencies: ['onprem'], available: true },
    ]
    const nullableString = { type: ['null', 'string'], maxLength: 200 }
    const schema = { properties: { evaluationModel: nullableString, evaluationJudgeModel: nullableString, evaluationMaxQuestions: { type: 'integer' } } }
    const doc = { paths: { '/api/settings/evaluation': { put: { requestBody: { content: { 'application/json': { schema } } } } } } }
    const fetch = vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? doc : url === '/api/gateway/models' ? models : evaluation)))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(PlatformResourceView, { props: { resource: 'settings' }, attachTo: document.body })
    await flushPromises()

    await wrapper.findAll('tbody tr').find((r) => r.text().includes(PLATFORM.settingsSections.evaluation))!.trigger('click')
    await flushPromises()
    expect(fetch.mock.calls.map(([url]) => url)).toContain('/api/gateway/models')
    const judge = document.body.querySelector<HTMLSelectElement>('select#platform-settings-evaluationJudgeModel')!
    expect([...judge.options].map((o) => o.value)).toEqual(['', 'gpt-oss'])
    expect(judge.value).toBe('')
    expect(document.body.querySelector<HTMLSelectElement>('select#platform-settings-evaluationModel')!.value).toBe('gpt-oss')
    expect(document.body.querySelector('input#platform-settings-evaluationMaxQuestions')).not.toBeNull()
    wrapper.unmount()
  })

  it('is found in search by English words', () => {
    const entries = searchEntries(navGroups)
    for (const query of ['member groups', 'sharing', 'municipality']) {
      expect(filterEntries(entries, query).map((e) => e.label)).toContain(PLATFORM.resources.organisation.label)
    }
  })
})
