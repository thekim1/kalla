import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { PLATFORM } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const C = PLATFORM.canary

const last = [
  { profileId: 'p1', profile: 'pilot-embedding', recorded: true, similarity: null, passed: null, checkedAt: null },
  { profileId: 'p2', profile: 'drifted', recorded: true, similarity: 0.5, passed: false, checkedAt: '2026-11-02T08:00:00Z' },
  { profileId: 'p3', profile: 'new-profile', recorded: false, similarity: null, passed: null, checkedAt: null },
]

const results = [
  { profileId: 'p1', profile: 'pilot-embedding', similarity: 0.99999, passed: true, checkedAt: '2026-11-03T12:00:00Z', error: null, detail: null },
  { profileId: 'p2', profile: 'drifted', similarity: null, passed: false, checkedAt: null, error: 'gateway_unavailable', detail: 'GatewayUnavailableException (HTTP 503)' },
]

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

function rowOf(wrapper: ReturnType<typeof mount>, name: string) {
  return wrapper.get('[data-test="embedding-canary"]').findAll('tbody tr').find((tr) => tr.text().includes(name))!
}

describe('Gateway models: embedding canary on demand (admin design §4.5, §5.8)', () => {
  it('shows the stored last check per profile without probing the gateway and runs the check only when the button is pressed', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const fetch = vi.fn((url: string, init?: RequestInit) => {
      if (url === '/api/gateway/canary-check' && init?.method === 'POST') return Promise.resolve(json(results))
      if (url === '/api/gateway/canary-check') return Promise.resolve(json(last))
      return Promise.resolve(json([]))
    })
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()
    // The panels are on the Models page's Embedding tab (admin design §5.10).
    await wrapper.get('#resource-gateway-models-tab-embedding').trigger('click')
    await flushPromises()

    const posted = () => fetch.mock.calls.filter(([url, init]) => url === '/api/gateway/canary-check' && init?.method === 'POST')
    expect(posted()).toHaveLength(0)
    expect(rowOf(wrapper, 'pilot-embedding').text()).toContain(C.neverChecked)
    expect(rowOf(wrapper, 'drifted').text()).toContain(C.failed)
    expect(rowOf(wrapper, 'drifted').text()).toContain('0,50000')
    expect(rowOf(wrapper, 'new-profile').text()).toContain(C.notRecorded)

    await wrapper.get('[data-test="canary-check"]').trigger('click')
    await flushPromises()

    expect(posted()).toHaveLength(1)
    const pilot = rowOf(wrapper, 'pilot-embedding').text()
    expect(pilot).toContain(C.passed)
    expect(pilot).toContain('0,99999')
    expect(pilot).toContain('2026')
    const drifted = rowOf(wrapper, 'drifted').text()
    expect(drifted).toContain(C.errors.gateway_unavailable)
    expect(drifted).toContain(C.notStored)
    expect(rowOf(wrapper, 'new-profile').text()).toContain(C.notRecorded)
    expect(fetch.mock.calls.some(([url]) => String(url).startsWith('/api/gateway/status'))).toBe(false)
    wrapper.unmount()
  })

  it('is not shown on other platform pages', async () => {
    setActivePinia(createPinia())
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(json([]))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'data-protection' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('[data-test="embedding-canary"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
