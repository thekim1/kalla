import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { COMMON } from '@/utils/labels'
import PlatformResourceView from './PlatformResourceView.vue'
import { resetOpenApiCache } from './schema'

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

describe('Platform objects: the generic list and form (admin design §5.8)', () => {
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

  it('drops a shown-once secret when the drawer closes', async () => {
    setActivePinia(createPinia())
    document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
    const profiles = [{ id: 'sp1', host: 'www.umea.se', presetId: 'sitevision', appliedVersion: 2, appliedAt: '2026-11-01T12:00:00Z' }]
    const doc = { paths: { '/api/site-profiles/{id}': { put: { requestBody: { content: { 'application/json': { schema: { properties: { presetId: { type: 'string' } } } } } } } } } }
    vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(json(
      url === '/openapi/v1.json' ? doc : url === '/api/site-profiles' ? profiles : url.endsWith('/rollback') ? { secret: 'shown-once' } : { profile: {} },
    ))))
    const wrapper = mount(PlatformResourceView, { props: { resource: 'site-profiles' }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    ;(document.body.querySelector('[data-test="action-rollback"]') as HTMLButtonElement).click()
    await flushPromises()
    expect(document.body.querySelector('[data-test="secret"]')!.textContent).toContain('shown-once')

    const close = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === COMMON.close && !b.getAttribute('aria-label'))!
    close.click()
    await flushPromises()
    expect((wrapper.vm as unknown as { secret: string | null }).secret).toBeNull()
    wrapper.unmount()
  })
})
