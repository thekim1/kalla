import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import AuditView from './AuditView.vue'

// The filters as read from the address (admin design §5.12 *Filters*; #1063): a route the tests set.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const replace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push: vi.fn(), replace }) }))

afterEach(() => {
  vi.restoreAllMocks()
  route.query = {}
  document.body.innerHTML = ''
})

async function open(query: Record<string, string>) {
  setActivePinia(createPinia())
  route.query = query
  const list = vi.spyOn(api.audit, 'list').mockResolvedValue({ items: [], next: null })
  const wrapper = mount(AuditView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return { wrapper, list }
}

describe('Audit log: an event from the address (#1063)', () => {
  it('brings its type of object, so only that type’s event is listed and the type chip says so', async () => {
    const { wrapper, list } = await open({ event: 'collection.publish' })
    expect(list.mock.calls[0]![0]).toEqual(expect.objectContaining({ action: 'publish', entityType: 'collection' }))
    expect((wrapper.get('[data-test="chip-Type of object"] select').element as HTMLSelectElement).value).toBe('collection')
    expect((wrapper.get('#audit-event').element as HTMLSelectElement).value).toBe('collection.publish')
    wrapper.unmount()
  })

  it('wins over a type of object it doesn’t belong to', async () => {
    const { wrapper, list } = await open({ entityType: 'source', event: 'collection.publish' })
    expect(list.mock.calls[0]![0]).toEqual(expect.objectContaining({ action: 'publish', entityType: 'collection' }))
    expect((wrapper.get('#audit-event').element as HTMLSelectElement).value).toBe('collection.publish')
    wrapper.unmount()
  })

  it('folds More filters again on Clear filters', async () => {
    const { wrapper } = await open({ service: 'kalla-worker' })
    const more = () => (wrapper.get('[data-test=more-filters]').element as HTMLDetailsElement).open
    expect(more()).toBe(true)
    await wrapper.get('[data-test=clear]').trigger('click')
    await flushPromises()
    expect(more()).toBe(false)
    wrapper.unmount()
  })
})
