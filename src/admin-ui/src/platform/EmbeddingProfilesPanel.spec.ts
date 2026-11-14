import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { EMBEDDING_PROFILES as E, PLATFORM } from '@/utils/labels'
import { ERROR_MESSAGES, embeddingTestMessage } from '@/utils/messages'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const profiles = [
  {
    id: 'p1', name: 'pilot-embedding', gatewayModel: 'text-embedding-3-large', dimensions: 1024, requestDimensions: false, maxInputTokens: 8191,
    maxResidency: 'Eu', isDefault: true, state: 'active', inUse: false, version: '7',
  },
  {
    id: 'p2', name: 'used', gatewayModel: 'bge-m3', dimensions: 1024, requestDimensions: false, maxInputTokens: 8192,
    maxResidency: 'Eu', isDefault: false, state: 'active', inUse: true, version: '3',
  },
]
const models = [
  { name: 'text-embedding-3-large', kind: 'model', modelKind: 'embedding', residencies: ['Eu'], available: true },
  { name: 'bge-m3', kind: 'model', modelKind: 'embedding', residencies: ['OnPrem'], available: true },
  { name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['OnPrem'], available: true },
]
const schema = {
  required: ['gatewayModel', 'maxResidency', 'version'],
  properties: {
    gatewayModel: { type: 'string', maxLength: 200 },
    dimensions: { type: ['integer', 'string'] },
    requestDimensions: { type: 'boolean' },
    maxInputTokens: { type: ['integer', 'string'] },
    maxResidency: { type: 'string' },
    version: { type: 'string' },
  },
}
const createSchema = {
  required: ['gatewayModel'],
  properties: {
    name: { type: ['null', 'string'] },
    gatewayModel: { type: 'string', maxLength: 200 },
    requestDimensions: { type: 'boolean' },
    dimensions: { type: ['null', 'integer', 'string'] },
    maxInputTokens: { type: ['null', 'integer', 'string'] },
    maxResidency: { type: ['null', 'string'] },
  },
}
const doc = {
  paths: {
    '/api/embedding-profiles/{id}': { put: { requestBody: { content: { 'application/json': { schema } } } } },
    '/api/embedding-profiles': { post: { requestBody: { content: { 'application/json': { schema: createSchema } } } } },
  },
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': `application/${status >= 400 ? 'problem+' : ''}json` } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

function stub(answers: { test?: unknown; put?: () => Response; post?: () => Response; list?: unknown[] } = {}) {
  const fetch = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/openapi/v1.json') return Promise.resolve(json(doc))
    if (url === '/api/gateway/models') return Promise.resolve(json(models))
    if (url === '/api/embedding-profiles' && init?.method === 'POST') return Promise.resolve(answers.post ? answers.post() : json(profiles[0], 201))
    if (url === '/api/embedding-profiles') return Promise.resolve(json(answers.list ?? profiles))
    if (url.endsWith('/test')) return Promise.resolve(json(answers.test))
    if (init?.method === 'PUT') return Promise.resolve(answers.put ? answers.put() : json(profiles[0]))
    return Promise.resolve(json([]))
  })
  vi.stubGlobal('fetch', fetch)
  return fetch
}

async function open(name: string) {
  setActivePinia(createPinia())
  document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
  const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
  await flushPromises()
  await wrapper.get(`[data-test="profile-edit-${name}"]`).trigger('click')
  await flushPromises()
  return wrapper
}

const body = (call: unknown[]) => JSON.parse(String((call[1] as RequestInit).body)) as Record<string, unknown>
const q = <T extends Element>(selector: string) => document.body.querySelector<T>(selector)

describe('Gateway models: embedding profiles editable before use (admin design §4.5, §5.8)', () => {
  it('lists the profiles with whether content uses them', async () => {
    setActivePinia(createPinia())
    stub()
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()
    const table = wrapper.get('[data-test="embedding-profiles"]')
    const rows = table.findAll('tbody tr')
    expect(rows[0]!.text()).toContain('pilot-embedding')
    expect(rows[0]!.text()).toContain(E.isDefault)
    expect(rows[0]!.text()).toContain(E.unused)
    expect(rows[1]!.text()).toContain(E.inUse)
    // The profiles come before the canary check on the page.
    const html = wrapper.html()
    expect(html.indexOf('data-test="embedding-profiles"')).toBeLessThan(html.indexOf('data-test="embedding-canary"'))
    wrapper.unmount()
  })

  it('edits a profile with an embedding model picker and a ceiling of OnPrem or Eu, and sends the version back', async () => {
    const fetch = stub()
    const wrapper = await open('pilot-embedding')
    const model = q<HTMLSelectElement>('select#embedding-profile-gatewayModel')!
    expect([...model.options].map((o) => o.value)).toEqual(['text-embedding-3-large', 'bge-m3'])
    const ceiling = q<HTMLSelectElement>('select#embedding-profile-maxResidency')!
    expect([...ceiling.options].map((o) => o.value)).toEqual(['OnPrem', 'Eu'])
    expect(q('[data-test=profile-in-use]')).toBeNull()

    const dims = q<HTMLInputElement>('input#embedding-profile-dimensions')!
    dims.value = '768'
    dims.dispatchEvent(new Event('input'))
    q<HTMLFormElement>('form#embedding-profile')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(put[0]).toBe('/api/embedding-profiles/p1')
    expect(body(put)).toMatchObject({ gatewayModel: 'text-embedding-3-large', dimensions: 768, maxResidency: 'Eu', version: '7' })
    wrapper.unmount()
  })

  it('tests the values as entered and says the outcome in plain words, without saving', async () => {
    const result = { ok: false, dimensions: 3072, residency: 'Eu', error: 'bad_vector', detail: null }
    const fetch = stub({ test: result })
    const wrapper = await open('pilot-embedding')
    const dims = q<HTMLInputElement>('input#embedding-profile-dimensions')!
    dims.value = '1536'
    dims.dispatchEvent(new Event('input'))

    q<HTMLButtonElement>('[data-test=profile-test-run]')!.click()
    await flushPromises()

    const post = fetch.mock.calls.find(([url]) => String(url).endsWith('/test'))!
    expect(post[0]).toBe('/api/embedding-profiles/p1/test')
    expect(body(post)).toEqual({ gatewayModel: 'text-embedding-3-large', dimensions: 1536, requestDimensions: false, maxResidency: 'Eu' })
    expect(q('[data-test=profile-test-result]')!.textContent).toBe(
      embeddingTestMessage(result, { gatewayModel: 'text-embedding-3-large', dimensions: 1536, maxResidency: 'Eu' }),
    )
    expect(q('[data-test=profile-test-result]')!.textContent).toContain('3072 dimensions')
    expect(fetch.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
    wrapper.unmount()
  })

  it('shows a used profile\'s fixed fields read-only, so the ceiling is the only field to change (#565)', async () => {
    stub()
    const wrapper = await open('used')
    expect(q<HTMLSelectElement>('select#embedding-profile-gatewayModel')!.disabled).toBe(true)
    expect(q<HTMLInputElement>('input#embedding-profile-dimensions')!.disabled).toBe(true)
    expect(q<HTMLInputElement>('input#embedding-profile-maxInputTokens')!.disabled).toBe(true)
    expect(q<HTMLButtonElement>('button[role=switch]')!.disabled).toBe(true)
    expect(q<HTMLSelectElement>('select#embedding-profile-maxResidency')!.disabled).toBe(false)
    wrapper.unmount()
  })

  it('names the dimensions switch by a visible label (#565)', async () => {
    stub()
    const wrapper = await open('pilot-embedding')
    const toggle = q<HTMLButtonElement>('button[role=switch]')!
    expect(toggle.disabled).toBe(false)
    const label = document.getElementById(toggle.getAttribute('aria-labelledby')!)!
    expect(label.textContent).toBe(PLATFORM.fields.requestDimensions)
    expect(toggle.hasAttribute('aria-label')).toBe(false)
    expect(document.getElementById(toggle.getAttribute('aria-describedby')!)!.textContent).toBe(PLATFORM.fieldHints.requestDimensions)
    wrapper.unmount()
  })

  it('says a used profile changes only its ceiling, and shows the server\'s refusal', async () => {
    stub({
      put: () => json({ title: 'Conflict', status: 409, code: 'profile_in_use', params: { profile: 'used' } }, 409),
    })
    const wrapper = await open('used')
    expect(q('[data-test=profile-in-use]')!.textContent).toBe(E.inUseNote)
    q<HTMLFormElement>('form#embedding-profile')!.dispatchEvent(new Event('submit'))
    await flushPromises()
    expect(document.body.textContent).toContain(ERROR_MESSAGES.profile_in_use!.replace('{profile}', 'used'))
    wrapper.unmount()
  })

  it('creates a profile from an embedding model, leaving out what the server takes from the model', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const fetch = stub({ list: [] })
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.get('[data-test=profiles-empty]').text()).toBe(E.none)
    await wrapper.get('[data-test=profile-create]').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain(E.createLead)
    const model = q<HTMLSelectElement>('select#embedding-profile-create-gatewayModel')!
    expect([...model.options].map((o) => o.value)).toEqual(['text-embedding-3-large', 'bge-m3'])
    const ceiling = q<HTMLSelectElement>('select#embedding-profile-create-maxResidency')!
    expect([...ceiling.options].map((o) => o.value)).toEqual(['', 'OnPrem', 'Eu'])
    expect(document.body.textContent).toContain(E.createHints.dimensions)
    model.value = 'bge-m3'
    model.dispatchEvent(new Event('change'))
    q<HTMLFormElement>('form#embedding-profile-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    const post = fetch.mock.calls.find(([, init]) => init?.method === 'POST')!
    expect(post[0]).toBe('/api/embedding-profiles')
    // Empty fields go as null: the server takes them from the model (or measures the size).
    expect(body(post)).toEqual({ name: null, gatewayModel: 'bge-m3', requestDimensions: false, dimensions: null, maxInputTokens: null, maxResidency: null })
    wrapper.unmount()
  })

  it('says why a create was refused when its test embedding failed', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const test = { ok: false, dimensions: null, residency: null, error: 'model_not_allowed', detail: null }
    stub({
      list: [],
      post: () => json({ title: 'Unprocessable', status: 422, code: 'profile_test_failed', params: { error: 'model_not_allowed' }, test }, 422),
    })
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-test=profile-create]').trigger('click')
    await flushPromises()
    const model = q<HTMLSelectElement>('select#embedding-profile-create-gatewayModel')!
    model.value = 'text-embedding-3-large'
    model.dispatchEvent(new Event('change'))
    q<HTMLFormElement>('form#embedding-profile-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    expect(q('[data-test=profile-create-result]')!.textContent).toBe(
      embeddingTestMessage(test, { gatewayModel: 'text-embedding-3-large', dimensions: 0, maxResidency: '' }),
    )
    wrapper.unmount()
  })
})
