import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Client } from '@/api/types'
import { LEVEL_LABEL, PLATFORM_CLIENTS } from '@/utils/labels'
import { platformEntries } from './resources'
import ClientsView from './ClientsView.vue'

const L = PLATFORM_CLIENTS

describe('Clients page (admin design §5.9)', () => {
  it('lists each client with its kind, information class, state and active keys, and opens its drawer', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.clients, 'list').mockResolvedValue([
      { id: 'c1', name: 'Open WebUI', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, activeKeys: 2 },
    ])
    const get = vi.spyOn(api.clients, 'get').mockResolvedValue({ id: 'c1', name: 'Open WebUI', kind: 'open-webui', assistants: [] } as unknown as Client)
    vi.spyOn(api.keys, 'list').mockResolvedValue([])
    const wrapper = mount(ClientsView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    const cells = wrapper.get('tbody tr').findAll('th, td').map((c) => c.text())
    expect(cells[0]).toContain('Open WebUI')
    expect(cells[1]).toContain(L.kinds['open-webui']!.title)
    expect(cells[2]).toContain(LEVEL_LABEL.internal)
    expect(cells[4]).toContain('2')

    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect(get).toHaveBeenCalledWith('c1')
    wrapper.unmount()
  })

  it('keeps Clients first on the Platform objects page, linked to its own page', () => {
    expect(platformEntries[0]).toMatchObject({ id: 'clients', icon: 'apps', to: { name: 'platform-clients' } })
    expect(platformEntries.filter((e) => e.id === 'clients')).toHaveLength(1)
  })
})
