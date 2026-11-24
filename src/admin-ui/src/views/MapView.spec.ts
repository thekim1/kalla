import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { MapRow } from '@/api/types'
import { department } from '@/test/fixtures'
import { FILTERS, MAP } from '@/utils/labels'
import MapView from './MapView.vue'

// `?department=` and `?show=` through a route the tests set; filter changes go to router.replace.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const replace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => ({ push: vi.fn(), replace }) }))

const own: MapRow = {
  collection: {
    id: 'c1', name: 'Blanketter', departmentId: 'd1', departmentName: 'Bygg och miljö', shared: false, sharedWith: 'department', classification: 'internal',
    published: true, contactName: 'B. Berg', contactEmail: null,
  },
  assistant: null,
  audience: null,
}
const sharedIn: MapRow = {
  collection: {
    id: 'c9', name: 'umea.se', departmentId: 'd9', departmentName: 'Kommunikation', shared: true, sharedWith: 'municipality', classification: 'public',
    published: true, contactName: 'K. Lund', contactEmail: null,
  },
  assistant: {
    id: 'a1', name: 'Permits assistant', state: 'active', publishedVersion: 3, draftOnly: false, topics: ['bygglov'], contactName: 'A. Andersson', contactEmail: null,
  },
  audience: { clientId: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui' },
}
const draftOnly: MapRow = { ...sharedIn, assistant: { ...sharedIn.assistant!, id: 'a2', name: 'Kundtjänst bygg', publishedVersion: null, draftOnly: true, topics: [] }, audience: null }

describe('Map (admin design §5.5.6, as a table)', () => {
  it('has a row per path with the responsible contact, and filters by department and by what to show', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([department({ id: 'd1', name: 'Bygg och miljö' }), department({ id: 'd2', name: 'Kultur' })])
    const map = vi.spyOn(api.organisation, 'map').mockImplementation(async (id) => ({
      departmentId: id, departmentName: id === 'd1' ? 'Bygg och miljö' : 'Kultur', rows: id === 'd1' ? [own, sharedIn, draftOnly] : [],
    }))

    const wrapper = mount(MapView, { global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    // All departments in scope by default.
    expect(map.mock.calls.map((c) => c[0])).toEqual(['d1', 'd2'])
    const rows = wrapper.findAll('[data-test=map] tbody tr').map((r) => r.text())
    expect(rows).toHaveLength(3)
    expect(rows[0]).toContain('Blanketter')
    expect(rows[0]).toContain('B. Berg') // no assistant: the knowledge base's contact
    expect(rows[1]).toContain('umea.se')
    expect(rows[1]).toContain('shared by Kommunikation')
    expect(rows[1]).toContain('Permits assistant')
    expect(rows[1]).toContain('version 3 · topics: bygglov')
    expect(rows[1]).toContain('Open WebUI (pilot)')
    expect(rows[1]).toContain('A. Andersson')
    expect(rows[2]).toContain('Kundtjänst bygg')
    expect(rows[2]).toContain('draft')
    // A row is a link to its assistant, or to the knowledge base when no assistant uses it (§5.5.2 "Lists open as links").
    expect(wrapper.findAllComponents(RouterLinkStub).map((l) => l.props('to'))).toEqual([
      { name: 'collection', params: { id: 'c1' } }, { name: 'assistant', params: { id: 'a1' } }, { name: 'assistant', params: { id: 'a2' } },
    ])

    // The department and what to show are chips kept in the address (admin design §5.12 Filters), with the count.
    expect(wrapper.get('[data-test=filter-count]').text()).toBe(MAP.shown(3))
    await wrapper.get('[data-test="chip-Show"] select').setValue('unlinked')
    expect(wrapper.findAll('[data-test=map] tbody tr')).toHaveLength(1)
    await wrapper.get('[data-test="chip-Show"] select').setValue('shared')
    expect(wrapper.findAll('[data-test=map] tbody tr')).toHaveLength(2)
    await flushPromises()
    expect(replace).toHaveBeenLastCalledWith({ query: { show: 'shared' } })

    await wrapper.get('[data-test="chip-Department"] select').setValue('d2')
    await flushPromises()
    expect(map).toHaveBeenLastCalledWith('d2')
    expect(wrapper.text()).toContain('Nothing to show for this choice.')
    expect(wrapper.get('[data-test="chip-Department"] [data-test=chip-remove]').attributes('aria-label')).toBe(FILTERS.remove('Department'))
    await wrapper.get('[data-test=clear-filters]').trigger('click')
    await flushPromises()
    expect((wrapper.get('[data-test="chip-Show"] select').element as HTMLSelectElement).value).toBe('all')
    expect(map.mock.calls.slice(-2).map((c) => c[0])).toEqual(['d1', 'd2']) // every department again
  })
})
