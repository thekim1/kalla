import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { formatNumber } from '@/utils/format'
import { PLATFORM } from '@/utils/labels'
import { PLATFORM_RULE_MESSAGES } from '@/utils/messages'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const models = [
  { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], source: 'catalog', contextWindow: null, maxOutputTokens: null, available: true, limitsSource: 'manual' },
  { name: 'ume/chat-se', kind: 'model', modelKind: 'chat', residencies: ['Se'], source: 'catalog', contextWindow: 200000, maxOutputTokens: 32000, available: true, limitsSource: 'catalog' },
]
const nullableInt = { type: ['null', 'integer', 'string'], minimum: 1, maximum: 10_000_000 }
const doc = {
  paths: {
    '/api/gateway/models/{name}': {
      put: { requestBody: { content: { 'application/json': { schema: { properties: { contextWindow: nullableInt, maxOutputTokens: nullableInt } } } } } },
    },
  },
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

function mountModels(respond: (url: string, init?: RequestInit) => Response | undefined) {
  setActivePinia(createPinia())
  document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
  const fetch = vi.fn((url: string, init?: RequestInit) => {
    const answer = respond(url, init)
    if (answer) return Promise.resolve(answer)
    if (url === '/openapi/v1.json') return Promise.resolve(json(doc))
    if (url === '/api/gateway/models') return Promise.resolve(json(models))
    return Promise.resolve(json([]))
  })
  vi.stubGlobal('fetch', fetch)
  return { fetch, wrapper: mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body }) }
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

describe('Gateway models: limits the gateway sends (admin design §4.10, #606)', () => {
  it('opens a catalog row\'s limits read-only with a note that the gateway sends them, and no form', async () => {
    const { fetch, wrapper } = mountModels(() => undefined)
    await flushPromises()

    const row = wrapper.findAll('tbody tr').find((r) => r.text().includes('ume/chat-se'))!
    expect(row.text()).toContain(PLATFORM.gatewayModels.limitsSources.catalog)
    await row.trigger('click')
    await flushPromises()

    expect(document.body.querySelector('form#platform-gateway-models')).toBeNull()
    expect(document.body.querySelector('input#platform-gateway-models-contextWindow')).toBeNull()
    const details = document.body.querySelector('[data-test="details"]')!
    expect(details.querySelector('h3')!.textContent).toBe(PLATFORM.gatewayModels.limits)
    const items = [...details.querySelectorAll('dt')].map((dt) => [dt.textContent, dt.nextElementSibling!.textContent])
    expect(items).toEqual([
      [PLATFORM.fields.contextWindow, formatNumber(200000)],
      [PLATFORM.fields.maxOutputTokens, formatNumber(32000)],
    ])
    expect(details.textContent).toContain(PLATFORM.gatewayModels.limitsFromGateway)
    expect(fetch.mock.calls.some(([url]) => url === '/openapi/v1.json')).toBe(false)
    wrapper.unmount()
  })

  it('keeps the edit form for a manual row, without the gateway\'s note', async () => {
    const { wrapper } = mountModels(() => undefined)
    await flushPromises()

    const row = wrapper.findAll('tbody tr').find((r) => r.text().includes('ume/chat-eu'))!
    expect(row.text()).toContain(PLATFORM.gatewayModels.limitsSources.manual)
    await row.trigger('click')
    await flushPromises()

    expect(document.body.querySelector('form#platform-gateway-models')).not.toBeNull()
    expect(document.body.querySelector('[data-test="details"]')).toBeNull()
    expect(document.body.textContent).not.toContain(PLATFORM.gatewayModels.limitsFromGateway)
    wrapper.unmount()
  })

  it('shows the platform rule\'s own message when a save comes back limits_from_gateway anyway', async () => {
    const { wrapper } = mountModels((_, init) =>
      init?.method === 'PUT' ? json({ status: 400, code: 'platform_rule', params: { constraint: 'limits_from_gateway' } }, 400) : undefined)
    await flushPromises()

    await wrapper.findAll('tbody tr').find((r) => r.text().includes('ume/chat-eu'))!.trigger('click')
    await flushPromises()
    document.body.querySelector<HTMLFormElement>('form#platform-gateway-models')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    expect(document.body.textContent).toContain(PLATFORM_RULE_MESSAGES.limits_from_gateway)
    expect(document.body.querySelector('form#platform-gateway-models')).not.toBeNull()
    wrapper.unmount()
  })
})

describe('Gateway models: the gateway\'s version and features (admin design §4.10)', () => {
  it('shows the version and features of the gateway\'s last context', async () => {
    const { fetch, wrapper } = mountModels((url) =>
      url === '/api/organisation/sync'
        ? json({ gateway: { version: '0.10.0', adminUrl: null, features: ['organisation', 'act-as-key'] }, degraded: null })
        : undefined)
    await flushPromises()

    const panel = wrapper.get('[data-test="gateway-context"]')
    expect(panel.text()).toContain(PLATFORM.gatewayModels.gateway)
    expect(panel.get('[data-test="gateway-version"]').text()).toBe('0.10.0')
    expect(panel.get('[data-test="gateway-features"]').text()).toBe('organisation, act-as-key')
    expect(fetch.mock.calls.some(([url]) => url === '/api/gateway/status')).toBe(false)
    wrapper.unmount()
  })

  it('shows nothing of the gateway when the sync has read no context, or its status cannot be read', async () => {
    const empty = mountModels((url) => (url === '/api/organisation/sync' ? json({ gateway: null, degraded: null }) : undefined))
    await flushPromises()
    expect(empty.wrapper.find('[data-test="gateway-context"]').exists()).toBe(false)
    empty.wrapper.unmount()

    const failed = mountModels((url) => (url === '/api/organisation/sync' ? json({}, 500) : undefined))
    await flushPromises()
    expect(failed.wrapper.find('[data-test="gateway-context"]').exists()).toBe(false)
    failed.wrapper.unmount()
  })
})

describe('Gateway models: the Integration API read-back (#654, admin design §4.5, §8.6)', () => {
  const sync = (integration: unknown) => (url: string) =>
    url === '/api/organisation/sync' ? json({ gateway: { version: '0.10.1', adminUrl: null, features: [] }, degraded: null, integration }) : undefined

  it('says the read-back is off when Gateway:IntegrationApiUrl is not set', async () => {
    const { wrapper } = mountModels(sync({ configured: false, lastError: null }))
    await flushPromises()
    const row = wrapper.get('[data-test="gateway-readback"]')
    expect(row.attributes('data-state')).toBe('off')
    expect(row.text()).toBe(PLATFORM.gatewayModels.readbackOff)
    wrapper.unmount()
  })

  it('says the read-back works when it is set and nothing was refused', async () => {
    const { wrapper } = mountModels(sync({ configured: true, lastError: null }))
    await flushPromises()
    const row = wrapper.get('[data-test="gateway-readback"]')
    expect(row.attributes('data-state')).toBe('on')
    expect(row.text()).toBe(PLATFORM.gatewayModels.readbackOn)
    wrapper.unmount()
  })

  it('shows the last refusal\'s reason, its status and where the fix is', async () => {
    const { wrapper } = mountModels(sync({ configured: true, lastError: { reason: 'scope_missing', status: 403, at: '2026-11-13T12:00:00Z' } }))
    await flushPromises()
    const row = wrapper.get('[data-test="gateway-readback"]')
    expect(row.attributes('data-state')).toBe('failed')
    expect(row.text()).toContain(`${PLATFORM.gatewayModels.readbackReasons.scope_missing} (HTTP 403).`)
    expect(row.text()).toContain(PLATFORM.gatewayModels.readbackHelp)
    wrapper.unmount()
  })

  it('names an unknown reason as a failure, and a missing status without HTTP', async () => {
    const { wrapper } = mountModels(sync({ configured: true, lastError: { reason: 'unavailable', status: null, at: '2026-11-13T12:00:00Z' } }))
    await flushPromises()
    expect(wrapper.get('[data-test="gateway-readback"]').text()).not.toContain('HTTP')
    wrapper.unmount()
  })
})
