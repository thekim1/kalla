import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { PLATFORM } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const models = [
  { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], source: 'catalog', contextWindow: null, maxOutputTokens: null, available: true },
]
const nullableInt = { type: ['null', 'integer', 'string'], minimum: 1, maximum: 10_000_000 }
const doc = {
  paths: {
    '/api/gateway/models/{name}': {
      put: { requestBody: { content: { 'application/json': { schema: { properties: { contextWindow: nullableInt, maxOutputTokens: nullableInt } } } } } },
    },
  },
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

describe('Gateway models: a model\'s limits (Decisions "Answer length from the model", admin design §4.5)', () => {
  it('shows each model\'s limits and saves them for the model, with a slash in its name', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const fetch = vi.fn((url: string, init?: RequestInit) => {
      if (url === '/openapi/v1.json') return Promise.resolve(json(doc))
      if (url === '/api/gateway/models') return Promise.resolve(json(models))
      if (init?.method === 'PUT') return Promise.resolve(json({ ...models[0], contextWindow: 128000, maxOutputTokens: 16384 }))
      return Promise.resolve(json([]))
    })
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()

    const header = wrapper.findAll('thead th').map((th) => th.text())
    expect(header).toContain(PLATFORM.fields.contextWindow)
    expect(header).toContain(PLATFORM.fields.maxOutputTokens)

    await wrapper.findAll('tbody tr').find((r) => r.text().includes('ume/chat-eu'))!.trigger('click')
    await flushPromises()
    const window = document.body.querySelector<HTMLInputElement>('input#platform-gateway-models-contextWindow')!
    const output = document.body.querySelector<HTMLInputElement>('input#platform-gateway-models-maxOutputTokens')!
    expect(window.value).toBe('')
    expect(document.body.textContent).toContain(PLATFORM.fieldHints.maxOutputTokens)
    window.value = '128000'
    window.dispatchEvent(new Event('input'))
    output.value = '16384'
    output.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLFormElement>('form#platform-gateway-models')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    const put = fetch.mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(put[0]).toBe('/api/gateway/models/ume%2Fchat-eu')
    expect(JSON.parse(String(put[1]!.body))).toEqual({ contextWindow: 128000, maxOutputTokens: 16384 })
    wrapper.unmount()
  })
})
