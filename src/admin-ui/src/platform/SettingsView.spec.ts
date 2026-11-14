import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { filterEntries, navGroups, searchEntries } from '@/router/nav'
import { PLATFORM, PROMPT_TEXTS, SETTINGS_PAGE } from '@/utils/labels'
import { resetOpenApiCache } from './schema'
import { SETTINGS_PAGE_SECTIONS } from './settingsPage'
import SettingsView from './SettingsView.vue'

const push = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

function mountPage(section?: string) {
  setActivePinia(createPinia())
  return mount(SettingsView, { props: { section }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

describe('Settings page (admin design §5.10)', () => {
  it('lists every section grouped by what it affects, and opens the first without a section in the address', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(json({ paths: {} }))))
    const wrapper = mountPage()
    await flushPromises()
    const nav = wrapper.get('[data-test=settings-sections]')
    expect(nav.findAll('h2').map((h) => h.text())).toEqual(Object.values(SETTINGS_PAGE.groups))
    const links = nav.findAllComponents(RouterLinkStub)
    expect(links.map((l: { text(): string }) => l.text())).toEqual(SETTINGS_PAGE_SECTIONS.map((s) => s.label))
    expect(links[0]!.props('to')).toEqual({ name: 'platform-settings', params: { section: 'chat' } })
    expect(links[0]!.attributes('aria-current')).toBe('page')
    expect(wrapper.get('#settings-section-title').text()).toBe(PLATFORM.settingsSections.chat)
    wrapper.unmount()
  })

  it('edits the crawl section on the page with the host politeness table instead of its JSON', async () => {
    const crawl = { version: '3', value: { allowedPorts: [8443], hostOverrides: { 'www.umea.se': { preset: 'fast' } } } }
    const doc = { paths: { '/api/settings/{section}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { value: { type: 'object' } } } } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/openapi/v1.json' ? doc : crawl))))
    const wrapper = mountPage('crawl')
    await flushPromises()
    expect(wrapper.get('#settings-section-title').text()).toBe(PLATFORM.settingsSections.crawl)
    expect(wrapper.text()).toContain(SETTINGS_PAGE.descriptions.crawl)
    expect(wrapper.find('[data-test=crawl-settings]').exists()).toBe(true)
    expect(wrapper.get('[data-test=host-politeness]').text()).toContain('www.umea.se')
    expect(wrapper.find('#settings-crawl-value').exists()).toBe(false)
    wrapper.unmount()
  })

  it('gives the evaluation section\'s model fields a picker over the mirror at the platform ceiling, and saves the section with its version (#466)', async () => {
    const evaluation = { evaluationModel: 'gpt-oss', evaluationJudgeModel: null, evaluationMaxQuestions: 100, version: '2' }
    const models = [
      { name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['onprem'], available: true },
      { name: 'rerank-1', kind: 'model', modelKind: 'rerank', residencies: ['onprem'], available: true },
    ]
    const nullableString = { type: ['null', 'string'], maxLength: 200 }
    const schema = { properties: { evaluationModel: nullableString, evaluationJudgeModel: nullableString, evaluationMaxQuestions: { type: 'integer' } } }
    const doc = { paths: { '/api/settings/evaluation': { put: { requestBody: { content: { 'application/json': { schema } } } } } } }
    const fetch = vi.fn((url: string, init?: RequestInit) =>
      Promise.resolve(json(url === '/openapi/v1.json' ? doc : url === '/api/gateway/models' ? models : init?.method === 'PUT' ? { ...evaluation, version: '3' } : evaluation)))
    vi.stubGlobal('fetch', fetch)
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const wrapper = mountPage('evaluation')
    await flushPromises()
    expect(fetch.mock.calls.map(([url]) => url)).toContain('/api/gateway/models')
    const judge = wrapper.get('select#settings-evaluation-evaluationJudgeModel').element as HTMLSelectElement
    expect([...judge.options].map((o) => o.value)).toEqual(['', 'gpt-oss'])
    expect((wrapper.get('select#settings-evaluation-evaluationModel').element as HTMLSelectElement).value).toBe('gpt-oss')

    await wrapper.get('input#settings-evaluation-evaluationMaxQuestions').setValue('50')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(put[0]).toBe('/api/settings/evaluation')
    expect(JSON.parse(String(put[1]!.body))).toMatchObject({ evaluationModel: 'gpt-oss', evaluationMaxQuestions: 50, version: '2' })
    wrapper.unmount()
  })

  it('edits the Search section\'s members as fields with their defaults, keeps the rest under Advanced, and leaves an emptied field out (admin design §5.10 step 2)', async () => {
    const value = { candidates: 150, maxChunksPerDocument: 2, rerankModel: 'gpt-oss', recency: null, futureMember: 'kept' }
    const schema = { properties: { value: { type: 'object' } } }
    const doc = { paths: { '/api/settings/retrieval': { put: { requestBody: { content: { 'application/json': { schema } } } } } } }
    const models = [{ name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['onprem'], available: true }]
    const fetch = vi.fn((url: string, init?: RequestInit) => Promise.resolve(json(
      url === '/openapi/v1.json' ? doc : url === '/api/gateway/models' ? models : init?.method === 'PUT' ? { value, version: '5' } : { value, version: '4' },
    )))
    vi.stubGlobal('fetch', fetch)
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const wrapper = mountPage('retrieval')
    await flushPromises()

    const input = (name: string) => wrapper.get(`#settings-retrieval-${name}`).element as HTMLInputElement
    expect(input('candidates').value).toBe('150')
    expect(input('vectorWeight').value).toBe('')
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.candidates)
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.minHnswSelectivity)
    // The JSON holds only what has no field, and is folded away.
    const advanced = wrapper.get('[data-test=advanced-json]')
    expect((advanced.element as HTMLDetailsElement).open).toBe(false)
    expect(JSON.parse((wrapper.get('#settings-retrieval-value').element as HTMLTextAreaElement).value)).toEqual({ recency: null, futureMember: 'kept' })

    await wrapper.get('#settings-retrieval-candidates').setValue('')
    await wrapper.get('#settings-retrieval-vectorWeight').setValue('0,5')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(JSON.parse(String(put[1]!.body))).toEqual({
      value: { recency: null, futureMember: 'kept', maxChunksPerDocument: 2, vectorWeight: 0.5, rerankModel: 'gpt-oss' }, version: '4',
    })
    wrapper.unmount()
  })

  it('edits the Processing section\'s default schedules as fields, and the chat section\'s link hosts one per line', async () => {
    const worker = { concurrency: { parse: 2 }, degradedFailureRatio: 0.05, scheduleJitterMinutes: 90, previewMaxUsd: 0.5, defaultSchedules: { crawl: '0 3 * * 0' }, version: '7' }
    const workerSchema = {
      properties: {
        concurrency: { type: 'object' }, degradedFailureRatio: { type: 'number' }, scheduleJitterMinutes: { type: 'integer' }, previewMaxUsd: { type: 'number' },
        defaultSchedules: { type: 'object' },
      },
    }
    const chat = { value: { allowedLinkHosts: ['umea.se', '*.umea.se'] }, version: '2' }
    const body = (schema: unknown) => ({ put: { requestBody: { content: { 'application/json': { schema } } } } })
    const doc = { paths: { '/api/settings/worker': body(workerSchema), '/api/settings/chat': body({ properties: { value: { type: 'object' } } }) } }
    const fetch = vi.fn((url: string, init?: RequestInit) => Promise.resolve(json(
      url === '/openapi/v1.json' ? doc : url === '/api/gateway/models' ? [] : url === '/api/settings/chat' ? chat : init?.method === 'PUT' ? worker : worker,
    )))
    vi.stubGlobal('fetch', fetch)
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const wrapper = mountPage('worker')
    await flushPromises()
    expect((wrapper.get('#settings-worker-crawl').element as HTMLInputElement).value).toBe('0 3 * * 0')
    expect((wrapper.get('#settings-worker-url-list').element as HTMLInputElement).value).toBe('')
    expect(wrapper.text()).toContain(PLATFORM.fieldHints['url-list'])
    await wrapper.get('#settings-worker-url-list').setValue('0 4 * * *')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(JSON.parse(String(put[1]!.body)).defaultSchedules).toEqual({ crawl: '0 3 * * 0', 'url-list': '0 4 * * *' })

    await wrapper.setProps({ section: 'chat' })
    await flushPromises()
    expect((wrapper.get('#settings-chat-allowedLinkHosts').element as HTMLTextAreaElement).value).toBe('umea.se\n*.umea.se')
    // Only the model fields need the gateway's models; the worker's schedules do not.
    expect(fetch.mock.calls.filter(([url]) => url === '/api/gateway/models')).toHaveLength(1)
    wrapper.unmount()
  })

  it('shows site profiles and data protection as sections with their lists, and the prompt texts as a section', async () => {
    const profiles = [{ id: 'sp1', host: 'www.umea.se', presetId: 'sitevision', appliedVersion: 2, appliedAt: '2026-11-01T12:00:00Z' }]
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(url === '/api/site-profiles' ? profiles : url.startsWith('/api/settings/chat/prompts') ? { sections: [] } : []))))
    const wrapper = mountPage('site-profiles')
    await flushPromises()
    expect(wrapper.get('#settings-section-title').text()).toBe(PLATFORM.resources.siteProfiles.label)
    expect(wrapper.get('[data-test=settings-section] tbody tr').text()).toContain('www.umea.se')
    // The page has the title; the list has no second one.
    expect(wrapper.findAll('h1')).toHaveLength(1)

    await wrapper.setProps({ section: 'prompt-texts' })
    await flushPromises()
    expect(wrapper.get('#settings-section-title').text()).toBe(PROMPT_TEXTS.title)
    expect(wrapper.find('[data-test=prompt-texts]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('is found in search by a section\'s name', () => {
    const entries = searchEntries(navGroups)
    expect(filterEntries(entries, 'website fetching').map((e) => e.to)).toContainEqual({ name: 'platform-settings', params: { section: 'crawl' } })
    expect(filterEntries(entries, 'data protection').map((e) => e.to)).toContainEqual({ name: 'platform-settings', params: { section: 'data-protection' } })
  })
})
