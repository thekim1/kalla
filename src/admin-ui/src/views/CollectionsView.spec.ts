import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { ApiError } from '@/api/client'
import CollectionCreateDrawer from '@/components/collections/CollectionCreateDrawer.vue'
import { useUiStore } from '@/stores/ui'
import { bffUser, collection, collectionSummary } from '@/test/fixtures'
import { button } from '@/test/helpers'
import CollectionsView from './CollectionsView.vue'

const push = vi.hoisted(() => vi.fn())
// `?q=` and `?view=` through a route the tests set (admin design §5.12 Filters); changes go to router.replace.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const replace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push, replace }) }))
const mountView = () => mount(CollectionsView, { global: { stubs: { RouterLink: RouterLinkStub } } })

describe('Knowledge bases (admin design §5.2, §4.5 view=)', () => {
  it('lists Ours, Shared with us and All, with owner, sharing, documents and topics', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-department-admin'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    const list = vi.spyOn(api.collections, 'list').mockImplementation(async (q) => ({
      total: 1,
      items: q?.view === 'shared'
        ? [collectionSummary({ id: 'c9', name: 'umea.se', departmentName: 'Kommunikation', shared: true, sharedWith: 'municipality', documentCount: 2323, topics: ['bygglov', 'politik'] })]
        : [collectionSummary({ name: 'Blanketter', departmentName: 'Bygg och miljö', sharedWith: 'selected', documentCount: 41 })],
    }))

    const wrapper = mountView()
    await flushPromises()
    // Admin design §5.7: the search has a placeholder and an aria-label, no label above it.
    const search = wrapper.get('[data-test=collections-search]')
    expect([search.attributes('placeholder'), search.attributes('aria-label')]).toEqual(['Search by name', 'Search knowledge bases by name'])
    expect(wrapper.find('label[for=collections-search]').exists()).toBe(false)

    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ view: 'mine' }))
    expect(wrapper.findAll('[role=tab]').map((t) => t.text())).toEqual(['Ours', 'Shared with us', 'All'])
    expect(wrapper.text()).toContain('Blanketter')
    expect(wrapper.text()).toContain('Bygg och miljö')
    expect(wrapper.text()).toContain('Selected departments')
    expect(wrapper.find('[data-test=shared]').exists()).toBe(false)

    await button(wrapper, 'Shared with us').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ view: 'shared' }))
    expect(wrapper.text()).toContain('umea.se')
    expect(wrapper.text()).toContain('Kommunikation')
    expect(wrapper.text()).toContain('Whole organisation')
    expect(wrapper.text()).toMatch(/2\s323/)
    expect(wrapper.text()).toContain('Topics: bygglov, politik')
    expect(wrapper.find('[data-test=shared]').exists()).toBe(true)

    await button(wrapper, 'All').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ view: 'all' }))
  })

  it('shows the live document count, the same one the Overview shows (#374)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-viewer'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 1, items: [collectionSummary({ name: 'umea.se', documentCount: 1166 })] })

    const wrapper = mountView()
    await flushPromises()

    const row = wrapper.findAll('tbody tr').find((r) => r.text().includes('umea.se'))!
    expect(row.text()).toMatch(/1\s166/)
  })

  it('says so when nothing is shared with the department', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser()
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 0, items: [] })

    const wrapper = mountView()
    await flushPromises()
    await button(wrapper, 'Shared with us').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('No other department shares a knowledge base with yours yet.')
  })

  it('opens a new knowledge base of files on the file list of Content, also when its source could not be made (§5.5.7)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-editor'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 0, items: [] })
    const wrapper = mountView()
    await flushPromises()
    const drawer = wrapper.findComponent(CollectionCreateDrawer)

    drawer.vm.$emit('created', collection({ id: 'c7', name: 'Riktlinjer' }), 'files', null)
    expect(push).toHaveBeenLastCalledWith({ name: 'collection', params: { id: 'c7' }, query: { tab: 'files' } })
    expect(useUiStore().toasts.at(-1)?.message).toBe('The knowledge base Riktlinjer has been created.')

    drawer.vm.$emit('created', collection({ id: 'c8', name: 'Rutiner' }), 'files', new ApiError(500, 'POST', { code: 'internal' }))
    expect(push).toHaveBeenLastCalledWith({ name: 'collection', params: { id: 'c8' }, query: { tab: 'files' } })
    expect(useUiStore().toasts.at(-1)?.message).toContain('Use Add files on the Sources tab.')

    drawer.vm.$emit('created', collection({ id: 'c9' }), 'web', null)
    expect(push).toHaveBeenLastCalledWith({ name: 'collection', params: { id: 'c9' } })
  })

  it('shows the last fetch linked to its run and the quality, and opens a row as a link (§5.5.2)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-viewer'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({
      total: 3,
      items: [
        collectionSummary({
          id: 'c1', name: 'Blanketter', lastRun: { id: 'r1', sourceId: 's1', state: 'degraded', startedAt: '2026-11-14T08:05:00Z', finishedAt: '2026-11-14T08:20:00Z' },
          latestEvaluation: { recallAtK: 0.8, finishedAt: '2026-11-12T03:00:00Z' },
        }),
        collectionSummary({ id: 'c2', name: 'Ny' }),
        collectionSummary({ id: 'c9', name: 'umea.se', shared: true }),
      ],
    })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.findAll('th[scope=col]').map((h) => h.text())).toEqual(
      ['Name', 'Department', 'Class', 'Sharing', 'Documents', 'Last fetch', 'Quality', 'State'],
    )
    const [measured, fresh, shared] = wrapper.findAll('tbody tr')
    const links = wrapper.findAllComponents(RouterLinkStub)
    expect(links.find((l) => l.attributes('data-test') === 'last-fetch-c1')!.props('to')).toEqual({ name: 'run', params: { id: 'r1' } })
    expect(measured!.get('[data-test=last-fetch-c1]').text()).toContain('14 Nov 2026')
    expect(measured!.get('[data-test=last-fetch-c1]').text()).toContain('Done with remarks')
    expect(measured!.get('[data-test=quality-c1]').text()).toContain('0,80')
    expect(measured!.get('[data-test=quality-c1]').text()).toContain('12 Nov 2026')
    expect(fresh!.text()).toContain('Never')
    expect(fresh!.text()).toContain('–')
    expect(shared!.text()).not.toContain('Never')

    expect(links.filter((l) => l.attributes('data-test') === 'row-link').map((l) => l.props('to'))).toEqual([
      { name: 'collection', params: { id: 'c1' } }, { name: 'collection', params: { id: 'c2' } }, { name: 'collection', params: { id: 'c9' } },
    ])
    // The run link navigates by itself; the rest of the row opens the knowledge base.
    await measured!.get('[data-test=last-fetch-c1]').trigger('click')
    expect(push).not.toHaveBeenCalledWith({ name: 'collection', params: { id: 'c1' } })
    await fresh!.findAll('td')[0]!.trigger('click')
    expect(push).toHaveBeenLastCalledWith({ name: 'collection', params: { id: 'c2' } })
  })

  it('keeps the search and the view in the address, so a link or a reload opens the same list (§5.12 Filters)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-department-admin'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    const list = vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 0, items: [] })
    route.query = { q: 'bygg', view: 'all' }
    const wrapper = mountView()
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ q: 'bygg', view: 'all' }))
    expect((wrapper.get('[data-test=collections-search]').element as HTMLInputElement).value).toBe('bygg')
    expect(wrapper.get('[role=tab][aria-selected=true]').text()).toBe('All')

    await wrapper.get('[data-test=collections-search]').setValue('bygglov')
    await flushPromises()
    expect(replace).toHaveBeenLastCalledWith({ query: { q: 'bygglov', view: 'all' } })
    route.query = {}
    wrapper.unmount()
  })
})
