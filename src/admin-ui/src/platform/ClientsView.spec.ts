import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { ApiKey, Client } from '@/api/types'
import { LEVEL_LABEL, PLATFORM_CLIENTS } from '@/utils/labels'
import { navGroups } from '@/router/nav'
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

  it('lists every key with its app on All keys, filters by status, and opens the key in its app\'s drawer', async () => {
    setActivePinia(createPinia())
    route.query = { tab: 'keys' }
    const key = (id: string, name: string, clientId: string, clientName: string, status: string, lastUsedAt: string | null = null) =>
      ({ id, name, clientId, clientName, status, prefix: `ume-rag-sk-${id}`, expiresAt: null, lastUsedAt }) as ApiKey
    vi.spyOn(api.clients, 'list').mockResolvedValue([])
    const list = vi.spyOn(api.keys, 'list').mockResolvedValue([
      key('k1', 'Webb key', 'c1', 'Webb', 'active', new Date().toISOString()), key('k2', 'Old key', 'c2', 'Intranät', 'revoked'),
    ])
    const get = vi.spyOn(api.clients, 'get').mockResolvedValue({ id: 'c2', name: 'Intranät', kind: 'integration', assistants: [] } as unknown as Client)
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0 })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.clients, 'connection').mockResolvedValue({ apiBaseUrl: null })
    const wrapper = mount(ClientsView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    // Opened from the old Keys page's address: All keys, every key the caller manages.
    expect(wrapper.get('#clients-tab-keys').attributes('aria-selected')).toBe('true')
    expect(list).toHaveBeenCalledWith()
    const rows = () => wrapper.get('[data-test=all-keys]').findAll('tbody tr')
    expect(rows()).toHaveLength(2)
    expect(rows()[0]!.text()).toContain('Webb')
    expect(rows()[0]!.text()).toContain('ume-rag-sk-k1')
    expect(rows()[0]!.text()).not.toContain(L.neverUsed)
    expect(rows()[1]!.text()).toContain(L.keyStatus.revoked)
    expect(rows()[1]!.text()).toContain(L.neverUsed)

    await wrapper.get('#all-keys-status').setValue('revoked')
    expect(rows()).toHaveLength(1)
    await rows()[0]!.trigger('click')
    await flushPromises()
    expect(get).toHaveBeenCalledWith('c2')
    expect(wrapper.findComponent({ name: 'ClientDrawer' }).props()).toMatchObject({ open: true, clientId: 'c2', keyId: 'k2' })

    await wrapper.get('#all-keys-status').setValue('expired')
    expect(wrapper.get('[data-test=all-keys]').text()).toContain(L.allKeys.noMatch)
    route.query = {}
    wrapper.unmount()
  })

  it('is the first page of the menu\'s Platform group (admin design §5.10)', () => {
    expect(navGroups.find((g) => g.id === 'platform')!.items[0]).toMatchObject({ name: 'platform-clients', icon: 'apps', label: 'Apps and keys' })
  })
})
