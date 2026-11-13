import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { ApiKey, Client } from '@/api/types'
import { PLATFORM_CLIENTS } from '@/utils/labels'
import ClientDrawer from './ClientDrawer.vue'

const L = PLATFORM_CLIENTS

const client: Client = {
  id: '0199-client', name: 'Integration webb', kind: 'integration', oAuthClientId: 'webb-app', classificationCeiling: 'public', storesHistory: false,
  clientRetentionDays: null, allowedOrigins: [], toolPolicy: 'read', portalUrl: null, isEnabled: true, updatedAt: '2026-11-13T10:00:00Z', updatedBy: 'anna@umea.se',
  version: '7', assistants: [{ id: 'a1', name: 'Bygglovsassistenten', departmentId: 'd1', published: true, slug: 'bygglov', classification: 'internal' }],
}
const key: ApiKey = {
  id: 'k1', clientId: client.id, clientName: client.name, teamId: null, kind: 'sk', name: 'Webb key', description: null, prefix: 'ume-rag-sk-ab12', status: 'active',
  assistantIds: [], collectionIds: [], allowedIpRanges: [], requestsPerMinute: null, isEnabled: true, createdAt: '2026-11-13T10:00:00Z', createdBy: null,
  expiresAt: '2027-01-31T22:59:59Z', revokedAt: null, graceUntil: null, rotatedToKeyId: null, lastUsedAt: null,
}

function mountDrawer() {
  return mount(ClientDrawer, { props: { open: true, clientId: client.id }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
}

describe('A client\'s drawer (admin design §5.9)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.spyOn(api.keys, 'list').mockResolvedValue([key, { ...key, id: 'k0', name: 'Old key', status: 'revoked', expiresAt: null }])
  })

  it('shows the facts, this client\'s keys and the assistants that use it', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue(client)
    const wrapper = mountDrawer()
    await flushPromises()
    expect(api.keys.list).toHaveBeenCalledWith(client.id)

    const facts = wrapper.get('[data-test=client-facts]')
    expect(facts.get('[data-test=copy-value]').text()).toBe(client.id)
    expect(facts.text()).toContain(L.kinds.integration!.title)

    const rows = wrapper.get('[data-test=client-keys]').findAll('tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Webb key')
    expect(rows[0]!.text()).toContain('ume-rag-sk-ab12')
    expect(rows[0]!.text()).toContain(L.keyStatus.active)
    expect(rows[0]!.find('[data-test=rotate-k1]').exists()).toBe(true)
    expect(rows[1]!.text()).toContain(L.keyStatus.revoked)
    expect(rows[1]!.text()).toContain(L.keys.never)
    expect(rows[1]!.find('[data-test=revoke-k0]').exists()).toBe(false)

    const usedBy = wrapper.get('[data-test=client-used-by]')
    const link = usedBy.getComponent(RouterLinkStub)
    expect(link.props('to')).toEqual({ name: 'assistant', params: { id: 'a1' } })
    expect(usedBy.text()).toContain('Bygglovsassistenten')
    wrapper.unmount()
  })

  it('says how to add the client to an assistant when none uses it', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue({ ...client, assistants: [] })
    const wrapper = mountDrawer()
    await flushPromises()
    expect(wrapper.get('[data-test=client-used-by]').text()).toContain(L.usedBy.empty)
    wrapper.unmount()
  })

  it('saves the fields of its kind with the kind fixed, keeping what the form does not show', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue(client)
    const update = vi.spyOn(api.clients, 'update').mockResolvedValue({ ...client, name: 'Webben', version: '8' })
    const wrapper = mountDrawer()
    await flushPromises()
    expect(wrapper.find('#client-edit-portalUrl').exists()).toBe(false)
    await wrapper.get('#client-edit-name').setValue('Webben')
    await wrapper.get('[data-test=client-edit]').trigger('submit')
    await flushPromises()
    expect(update).toHaveBeenCalledWith(client.id, {
      name: 'Webben', kind: 'integration', classificationCeiling: 'public', storesHistory: false, clientRetentionDays: null, oAuthClientId: 'webb-app',
      portalUrl: null, isEnabled: true, allowedOrigins: [], toolPolicy: 'read', version: '7',
    })
    expect(wrapper.emitted('saved')).toHaveLength(1)
    // Who uses it stays, though the update's answer may leave it out.
    expect(wrapper.get('[data-test=client-used-by]').text()).toContain('Bygglovsassistenten')
    wrapper.unmount()
  })

  it('shows a revealed secret once and opens the key form in place for a new key', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue(client)
    vi.spyOn(api.keys, 'reveal').mockResolvedValue({ secret: 'ume-rag-sk-shown' })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0 })
    vi.spyOn(api.collections, 'list').mockResolvedValue({ items: [], total: 0 })
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.get('[data-test=reveal-k1]').trigger('click')
    await flushPromises()
    expect(api.keys.reveal).toHaveBeenCalledWith('k1')
    expect(wrapper.get('[data-test=secret]').text()).toContain('ume-rag-sk-shown')

    await wrapper.get('[data-test=new-key]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=secret]').exists()).toBe(false)
    expect(wrapper.find('[data-test=key-form]').exists()).toBe(true)
    // The client is this one: no client picker.
    expect(wrapper.find('#client-key-clientId').exists()).toBe(false)
    wrapper.unmount()
  })
})
