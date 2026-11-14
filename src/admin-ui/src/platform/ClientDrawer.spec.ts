import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { ApiKey, AssistantSummary, Client } from '@/api/types'
import { PLATFORM_CLIENTS } from '@/utils/labels'
import ClientDrawer from './ClientDrawer.vue'

const L = PLATFORM_CLIENTS

const client: Client = {
  id: '0199-client', name: 'Integration webb', kind: 'integration', oAuthClientId: 'webb-app', classificationCeiling: 'public', storesHistory: false,
  clientRetentionDays: null, allowedOrigins: [], toolPolicy: 'read', portalUrl: null, isEnabled: true, isManagedByDeployment: false, updatedAt: '2026-11-13T10:00:00Z', updatedBy: 'anna@umea.se',
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

const assistant = (id: string, name: string, classification: AssistantSummary['classification']): AssistantSummary => ({
  id, slug: name.toLowerCase(), name, departmentId: 'd1', classification, state: 'active', published: true, hasDraft: false, generalKnowledge: null,
})

describe('An app\'s drawer (admin design §5.9, §5.9.1)', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.spyOn(api.keys, 'list').mockResolvedValue([
      { ...key, lastUsedAt: '2026-11-01T08:00:00Z' }, { ...key, id: 'k0', name: 'Old key', status: 'revoked', expiresAt: null },
    ])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [assistant('a1', 'Bygglovsassistenten', 'public'), assistant('a2', 'Personal', 'internal')], total: 2 })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.clients, 'connection').mockResolvedValue({ apiBaseUrl: 'https://rag.example.se' })
  })

  it('shows the facts, this app\'s keys with their last use, the assistants it offers and how to connect', async () => {
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
    // A key that was used shows when; one that never was says so.
    expect(rows[0]!.text()).not.toContain(L.neverUsed)
    expect(rows[1]!.text()).toContain(L.neverUsed)

    // The assistants it offers are ticked; one above its class (public) cannot be.
    const offered = wrapper.get('[data-test=client-assistants]')
    expect((offered.get('[data-test=assistant-a1] input').element as HTMLInputElement).checked).toBe(true)
    expect((offered.get('[data-test=assistant-a2] input').element as HTMLInputElement).disabled).toBe(true)

    // How to connect, with no key on screen: where to find one, and placeholders in the call.
    const connect = wrapper.get('[data-test=client-connect]')
    expect(connect.get('[data-test=connect-address] [data-test=copy-value]').text()).toBe('https://rag.example.se/v1')
    expect(connect.get('[data-test=connect-no-key]').text()).toBe(L.connect.showKey)
    expect(connect.get('[data-test=connect-models] [data-test=copy-value]').text()).toBe('bygglov')
    expect(connect.get('[data-test=connect-example] [data-test=copy-value]').text()).toContain('Bearer <key>')
    wrapper.unmount()
  })

  it('saves the assistants the app offers from its side, and says so when it offers none', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue(client)
    const set = vi.spyOn(api.clients, 'setAssistants').mockResolvedValue({ ...client, assistants: [], version: '8' })
    const wrapper = mountDrawer()
    await flushPromises()
    await wrapper.get('[data-test=assistant-a1] input').setValue(false)
    await wrapper.get('[data-test=save-assistants]').trigger('click')
    await flushPromises()
    expect(set).toHaveBeenCalledWith(client.id, { assistantIds: [], version: '7' })
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.get('[data-test=nothing-picked]').text()).toBe(L.assistants.nothingPicked)
    expect(wrapper.get('[data-test=connect-models]').text()).toContain(L.connect.noModels)
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
    wrapper.unmount()
  })

  it('opens at the key it was asked for (a row of All keys), with the key form and this app\'s assistants to limit it to', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue(client)
    vi.spyOn(api.collections, 'list').mockResolvedValue({ items: [], total: 0 })
    const wrapper = mount(ClientDrawer, {
      props: { open: true, clientId: client.id, keyId: 'k1' }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } },
    })
    await flushPromises()
    expect(wrapper.text()).toContain(L.keys.editTitle('Webb key'))
    expect((wrapper.get('#client-key-name').element as HTMLInputElement).value).toBe('Webb key')
    const scoped = wrapper.get('[data-test=scope-assistants]')
    expect(scoped.findAll('input[type=checkbox]')).toHaveLength(1)
    expect(scoped.text()).toContain('Bygglovsassistenten')
    wrapper.unmount()
  })

  it('shows the deployment\'s own Open WebUI app as managed, with no New key and no key actions (identity design §5.2)', async () => {
    vi.spyOn(api.clients, 'get').mockResolvedValue({ ...client, kind: 'open-webui', isManagedByDeployment: true })
    const wrapper = mountDrawer()
    await flushPromises()
    expect(wrapper.get('[data-test=managed]').text()).toBe(L.managed)
    expect(wrapper.get('[data-test=managed-note]').text()).toBe(L.managedNote)
    expect(wrapper.find('[data-test=new-key]').exists()).toBe(false)
    expect(wrapper.find('[data-test=rotate-k1]').exists()).toBe(false)
    expect(wrapper.find('[data-test=revoke-k1]').exists()).toBe(false)
    // How to connect says the deployment does it; the assistants it offers are still chosen here.
    expect(wrapper.get('[data-test=connect-open-webui]').text()).toBe(L.connect.openWebUi)
    expect(wrapper.find('[data-test=client-assistants]').exists()).toBe(true)
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
    expect(wrapper.get('[data-test=client-keys] [data-test=secret]').text()).toContain('ume-rag-sk-shown')
    // The call to try now carries the key that is on screen.
    expect(wrapper.get('[data-test=connect-example] [data-test=copy-value]').text()).toContain('Bearer ume-rag-sk-shown')

    await wrapper.get('[data-test=new-key]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=secret]').exists()).toBe(false)
    expect(wrapper.find('[data-test=key-form]').exists()).toBe(true)
    // A new key asks for its name; the rest is collapsed.
    expect((wrapper.get('[data-test=key-restrictions]').element as HTMLDetailsElement).open).toBe(false)
    wrapper.unmount()
  })
})
