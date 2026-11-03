import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { PLATFORM } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

const C = PLATFORM.canary

const status = {
  configured: true,
  profiles: [
    { id: 'p1', name: 'pilot-embedding', canaryRecorded: true, canaryCheckedAt: null, canarySimilarity: null },
    { id: 'p2', name: 'drifted', canaryRecorded: true, canaryCheckedAt: '2026-11-02T08:00:00Z', canarySimilarity: 0.5 },
    { id: 'p3', name: 'new-profile', canaryRecorded: false, canaryCheckedAt: null, canarySimilarity: null },
  ],
}

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
  it('shows the last check per profile and runs the check only when the button is pressed', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const fetch = vi.fn((url: string, init?: RequestInit) => {
      if (url === '/api/gateway/canary-check' && init?.method === 'POST') return Promise.resolve(json(results))
      if (url === '/api/gateway/status') return Promise.resolve(json(status))
      return Promise.resolve(json([]))
    })
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(PlatformResourceView, { props: { resource: 'gateway-models' }, attachTo: document.body })
    await flushPromises()

    expect(fetch.mock.calls.some(([url]) => url === '/api/gateway/canary-check')).toBe(false)
    expect(rowOf(wrapper, 'pilot-embedding').text()).toContain(C.neverChecked)
    expect(rowOf(wrapper, 'drifted').text()).toContain(C.failed)
    expect(rowOf(wrapper, 'drifted').text()).toContain('0,50000')
    expect(rowOf(wrapper, 'new-profile').text()).toContain(C.notRecorded)

    await wrapper.get('[data-test="canary-check"]').trigger('click')
    await flushPromises()

    const posts = fetch.mock.calls.filter(([url, init]) => url === '/api/gateway/canary-check' && init?.method === 'POST')
    expect(posts).toHaveLength(1)
    const pilot = rowOf(wrapper, 'pilot-embedding').text()
    expect(pilot).toContain(C.passed)
    expect(pilot).toContain('0,99999')
    expect(pilot).toContain('2026')
    const drifted = rowOf(wrapper, 'drifted').text()
    expect(drifted).toContain(C.errors.gateway_unavailable)
    expect(drifted).toContain(C.notStored)
    expect(rowOf(wrapper, 'new-profile').text()).toContain(C.notRecorded)
    wrapper.unmount()
  })

  it('is not shown on other platform pages', async () => {
    setActivePinia(createPinia())
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(json([]))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'clients' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.find('[data-test="embedding-canary"]').exists()).toBe(false)
    wrapper.unmount()
  })
})
