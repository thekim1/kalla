import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Client } from '@/api/types'
import { LEVEL_LABEL, PLATFORM_CLIENTS } from '@/utils/labels'
import { platformEntries } from './resources'
import ClientsView from './ClientsView.vue'

const L = PLATFORM_CLIENTS
const route = { query: {} as Record<string, string> }
const replace = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRoute: () => route, useRouter: () => ({ replace }) }))

describe('Apps and keys page (admin design §5.9, §5.9.1)', () => {
  it('lists each app with its kind, information class, state, active keys and last use, and opens its drawer', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.clients, 'list').mockResolvedValue([
      { id: 'c1', name: 'Open WebUI', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, activeKeys: 2, lastUsedAt: null },
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
    expect(cells[5]).toContain(L.neverUsed)

    await wrapper.get('tbody tr').trigger('click')
    await flushPromises()
    expect(get).toHaveBeenCalledWith('c1')
    wrapper.unmount()
  })

  it('opens Add app with the assistant ticked when it comes from an assistant page', async () => {
    setActivePinia(createPinia())
    route.query = { connect: 'a1' }
    vi.spyOn(api.clients, 'list').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0 })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.clients, 'connection').mockResolvedValue({ apiBaseUrl: null })
    const wrapper = mount(ClientsView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(wrapper.findComponent({ name: 'ClientCreateDrawer' }).props()).toMatchObject({ open: true, assistantId: 'a1' })
    expect(replace).toHaveBeenCalledWith({ query: { connect: undefined } })
    route.query = {}
    wrapper.unmount()
  })

  it('keeps Clients first on the Platform objects page, linked to its own page', () => {
    expect(platformEntries[0]).toMatchObject({ id: 'clients', icon: 'apps', to: { name: 'platform-clients' } })
    expect(platformEntries.filter((e) => e.id === 'clients')).toHaveLength(1)
  })
})
