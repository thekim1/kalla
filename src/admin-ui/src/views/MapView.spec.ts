import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { MapRow } from '@/api/types'
import { department } from '@/test/fixtures'
import MapView from './MapView.vue'

vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

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

    const wrapper = mount(MapView)
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

    await wrapper.get('#map-show').setValue('unlinked')
    expect(wrapper.findAll('[data-test=map] tbody tr')).toHaveLength(1)
    await wrapper.get('#map-show').setValue('shared')
    expect(wrapper.findAll('[data-test=map] tbody tr')).toHaveLength(2)

    await wrapper.get('#map-department').setValue('d2')
    await flushPromises()
    expect(map).toHaveBeenLastCalledWith('d2')
    expect(wrapper.text()).toContain('Nothing to show for this choice.')
  })
})
