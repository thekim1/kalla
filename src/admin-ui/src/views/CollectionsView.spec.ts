import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
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
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))

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

    const wrapper = mount(CollectionsView)
    await flushPromises()

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

    const wrapper = mount(CollectionsView)
    await flushPromises()

    const row = wrapper.findAll('tbody tr').find((r) => r.text().includes('umea.se'))!
    expect(row.text()).toMatch(/1\s166/)
  })

  it('says so when nothing is shared with the department', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser()
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 0, items: [] })

    const wrapper = mount(CollectionsView)
    await flushPromises()
    await button(wrapper, 'Shared with us').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('No other department shares a knowledge base with yours yet.')
  })

  it('opens a new knowledge base of files on its Files tab, also when its source could not be made (§5.5.7)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser({ roles: ['rag-editor'] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 0, items: [] })
    const wrapper = mount(CollectionsView)
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
})
