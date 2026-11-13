import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { ApiKey, AssistantSummary, Client } from '@/api/types'
import { COMMON, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientCreateDrawer from './ClientCreateDrawer.vue'

const L = PLATFORM_CLIENTS

async function open(assistantId: string | null = null) {
  const wrapper = mount(ClientCreateDrawer, { props: { open: false, assistantId }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

async function submit(wrapper: VueWrapper) {
  await wrapper.get('#client-new').trigger('submit')
  await flushPromises()
}

async function pick(wrapper: VueWrapper, kind: string) {
  await wrapper.get(`#client-new-kind-${kind}`).trigger('change')
  await submit(wrapper)
}

const switchOf = (wrapper: VueWrapper, id: string) => wrapper.get(`#${id}`)

const client: Client = {
  id: '0199-client', name: 'Pilot', kind: 'open-webui', oAuthClientId: null, classificationCeiling: 'internal', storesHistory: true, clientRetentionDays: 90,
  allowedOrigins: [], toolPolicy: 'none', portalUrl: null, isEnabled: true, updatedAt: '2026-11-13T10:00:00Z', updatedBy: null, version: '1', assistants: [],
}
const key = { id: 'k1', name: 'Pilot key', prefix: 'ume-rag-sk-ab', status: 'active' } as ApiKey
const assistant = (id: string, name: string, classification: AssistantSummary['classification'], published = true): AssistantSummary => ({
  id, slug: name.toLowerCase(), name, departmentId: 'd1', classification, state: 'active', published, hasDraft: false, generalKnowledge: null,
})
const assistants = [assistant('a1', 'Bygglov', 'public'), assistant('a2', 'Personal', 'internal', false), assistant('a3', 'Journal', 'sensitive')]
const ticked = (wrapper: VueWrapper, id: string) => wrapper.get(`[data-test=assistant-${id}] input`).element as HTMLInputElement

describe('Add app (admin design §5.9, §5.9.1)', () => {
  beforeEach(() => {
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: assistants, total: assistants.length })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([{ id: 'd1', name: 'Bygg', costCenterCode: '10', isActive: true, syncedAt: '2026-11-13T10:00:00Z', teams: [] }])
    vi.spyOn(api.clients, 'connection').mockResolvedValue({ apiBaseUrl: 'https://rag.example.se' })
  })

  it('offers Open WebUI, integration and internal, and shows the later kinds disabled', async () => {
    const wrapper = await open()
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(1, 4, L.steps.kind))
    for (const kind of ['open-webui', 'integration', 'internal']) {
      expect((wrapper.get(`#client-new-kind-${kind}`).element as HTMLInputElement).disabled).toBe(false)
    }
    for (const kind of ['widget', 'own-client', 'teams', 'mcp-client', 'entra-app']) {
      const radio = wrapper.get(`#client-new-kind-${kind}`)
      expect((radio.element as HTMLInputElement).disabled).toBe(true)
      expect(radio.element.closest('label')!.textContent).toContain(L.later)
    }
    expect(wrapper.text()).toContain(L.kinds['open-webui']!.description)

    // Next without a kind stays on the step.
    await submit(wrapper)
    expect(wrapper.text()).toContain(L.kindRequired)
    wrapper.unmount()
  })

  it('shows only the fields of the chosen kind, with that kind\'s defaults', async () => {
    const wrapper = await open()
    await pick(wrapper, 'open-webui')
    expect((wrapper.get('#client-new-classificationCeiling').element as HTMLSelectElement).value).toBe('internal')
    expect(switchOf(wrapper, 'client-new-storesHistory').attributes('aria-checked')).toBe('true')
    expect((wrapper.get('#client-new-clientRetentionDays').element as HTMLInputElement).value).toBe('90')
    expect(wrapper.find('#client-new-portalUrl').exists()).toBe(true)
    expect(wrapper.find('#client-new-oAuthClientId').exists()).toBe(false)
    expect(switchOf(wrapper, 'client-new-isEnabled').attributes('aria-checked')).toBe('true')

    await wrapper.get('[data-test=back]').trigger('click')
    await pick(wrapper, 'internal')
    expect((wrapper.get('#client-new-classificationCeiling').element as HTMLSelectElement).value).toBe('sensitive')
    expect(wrapper.find('#client-new-storesHistory').exists()).toBe(false)
    expect(wrapper.find('#client-new-clientRetentionDays').exists()).toBe(false)
    expect(wrapper.find('#client-new-portalUrl').exists()).toBe(false)
    expect(wrapper.text()).toContain(L.hints.oAuthClientId)

    await wrapper.get('[data-test=back]').trigger('click')
    await pick(wrapper, 'integration')
    expect((wrapper.get('#client-new-classificationCeiling').element as HTMLSelectElement).value).toBe('public')
    expect(switchOf(wrapper, 'client-new-storesHistory').attributes('aria-checked')).toBe('false')
    expect(wrapper.find('#client-new-oAuthClientId').exists()).toBe(true)
    expect(wrapper.find('#client-new-portalUrl').exists()).toBe(false)
    wrapper.unmount()
  })

  it('turns chat history off and disables it, with the reason, at sensitive', async () => {
    const wrapper = await open()
    await pick(wrapper, 'open-webui')
    await wrapper.get('#client-new-classificationCeiling').setValue('sensitive')
    const history = switchOf(wrapper, 'client-new-storesHistory')
    expect(history.attributes('disabled')).toBeDefined()
    expect(history.attributes('aria-checked')).toBe('false')
    expect(wrapper.get('[data-test=stores-history]').text()).toContain(L.historySensitive)
    wrapper.unmount()
  })

  it('creates the client with its first key, then shows the secret, the id and the Open WebUI stack lines once', async () => {
    const create = vi.spyOn(api.clients, 'create').mockResolvedValue({ client, key: { key, secret: 'ume-rag-sk-secret' } })
    const wrapper = await open()
    await pick(wrapper, 'open-webui')
    await wrapper.get('#client-new-name').setValue('Pilot')
    await wrapper.get('#client-new-portalUrl').setValue('https://chat.example.invalid')
    await submit(wrapper)
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(3, 4, L.steps.assistants))
    await submit(wrapper)
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(4, 4, L.steps.firstKey))
    expect((wrapper.get('#client-new-key-name').element as HTMLInputElement).value).toBe(L.firstKey.defaultName('Pilot'))
    await submit(wrapper)

    expect(create).toHaveBeenCalledWith({
      name: 'Pilot', kind: 'open-webui', classificationCeiling: 'internal', storesHistory: true, clientRetentionDays: 90, oAuthClientId: null,
      portalUrl: 'https://chat.example.invalid', isEnabled: true, allowedOrigins: [], firstKey: { name: 'Pilot key' }, assistantIds: [],
    })
    expect(wrapper.emitted('created')?.[0]).toEqual([client])
    const result = wrapper.get('[data-test=client-created]')
    expect(result.get('[data-test=secret]').text()).toContain('ume-rag-sk-secret')
    expect(result.get('[data-test=client-id]').text()).toContain('0199-client')
    const lines = result.get('[data-test=stack-lines]')
    expect(lines.get('[data-test=copy-value]').text()).toBe('UME_RAG_OPENWEBUI_API_KEY=ume-rag-sk-secret\nUME_RAG_OPENWEBUI_CLIENT_ID=0199-client')
    expect(lines.findAll('[data-test=copy]')).toHaveLength(1)
    expect(lines.text()).toContain(L.connect.stackSentence)
    // Open WebUI gets the stack lines, not an address or a call to try.
    expect(result.find('[data-test=connect-example]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('creates a client without a key when the first key is turned off, and shows no stack lines', async () => {
    const create = vi.spyOn(api.clients, 'create').mockResolvedValue({ client: { ...client, kind: 'internal', classificationCeiling: 'sensitive' }, key: null })
    const wrapper = await open()
    await pick(wrapper, 'internal')
    await wrapper.get('#client-new-name').setValue('Tools')
    await submit(wrapper)
    await submit(wrapper)
    await wrapper.get('[data-test=create-key] input').setValue(false)
    await submit(wrapper)

    expect(create.mock.calls[0]![0]).toMatchObject({ kind: 'internal', classificationCeiling: 'sensitive', storesHistory: false, clientRetentionDays: null, firstKey: null })
    expect(wrapper.find('[data-test=secret]').exists()).toBe(false)
    expect(wrapper.find('[data-test=stack-lines]').exists()).toBe(false)
    expect(wrapper.text()).toContain(L.connect.noKey)
    wrapper.unmount()
  })

  it('checks the name and the feedback days before the key step', async () => {
    const wrapper = await open()
    await pick(wrapper, 'open-webui')
    await submit(wrapper)
    expect(wrapper.text()).toContain(L.nameRequired)
    await wrapper.get('#client-new-name').setValue('Pilot')
    await wrapper.get('#client-new-clientRetentionDays').setValue('ninety')
    await submit(wrapper)
    expect(wrapper.text()).toContain(PLATFORM.form.invalidNumber)
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(2, 4, L.steps.fields))
    wrapper.unmount()
  })

  it('picks the assistants the app offers, with those above its information class disabled, and shows how to connect', async () => {
    const made: Client = {
      ...client, kind: 'integration', classificationCeiling: 'internal', storesHistory: false,
      assistants: [
        { id: 'a2', name: 'Personal', departmentId: 'd1', published: false, slug: 'personal', classification: 'internal' },
        { id: 'a1', name: 'Bygglov', departmentId: 'd1', published: true, slug: 'bygglov', classification: 'public' },
      ],
    }
    const create = vi.spyOn(api.clients, 'create').mockResolvedValue({ client: made, key: { key, secret: 'ume-rag-sk-secret' } })
    const wrapper = await open()
    await pick(wrapper, 'integration')
    await wrapper.get('#client-new-name').setValue('Webb')
    await wrapper.get('#client-new-classificationCeiling').setValue('internal')
    await submit(wrapper)

    // Nothing ticked yet, and the sensitive assistant cannot be: the row says why (V7).
    expect(wrapper.get('[data-test=nothing-picked]').text()).toBe(L.assistants.nothingPicked)
    expect(ticked(wrapper, 'a3').disabled).toBe(true)
    expect(wrapper.get('[data-test=assistant-a3]').text()).toContain(L.assistants.aboveCeiling)
    expect(wrapper.get('[data-test=assistant-a2]').text()).toContain('Bygg')
    expect(wrapper.get('[data-test=assistant-a2]').text()).toContain(L.assistants.draft)
    await wrapper.get('[data-test=assistant-a1] input').setValue(true)
    await wrapper.get('[data-test=assistant-a2] input').setValue(true)
    expect(wrapper.find('[data-test=nothing-picked]').exists()).toBe(false)

    // Back to a lower class: the assistant above it is unticked when the step is reached again.
    await wrapper.get('[data-test=back]').trigger('click')
    await wrapper.get('#client-new-classificationCeiling').setValue('public')
    await submit(wrapper)
    expect(ticked(wrapper, 'a1').checked).toBe(true)
    expect(ticked(wrapper, 'a2').checked).toBe(false)
    await wrapper.get('[data-test=back]').trigger('click')
    await wrapper.get('#client-new-classificationCeiling').setValue('internal')
    await submit(wrapper)
    await wrapper.get('[data-test=assistant-a2] input').setValue(true)
    await submit(wrapper)
    await submit(wrapper)
    expect(create.mock.calls[0]![0]).toMatchObject({ kind: 'integration', classificationCeiling: 'internal', assistantIds: ['a1', 'a2'], firstKey: { name: 'Webb key' } })

    // How to connect: the address, the key, each model name, and a call with the first published assistant.
    const result = wrapper.get('[data-test=how-to-connect]')
    expect(result.get('[data-test=connect-address] [data-test=copy-value]').text()).toBe('https://rag.example.se/v1')
    expect(result.get('[data-test=secret]').text()).toContain('ume-rag-sk-secret')
    expect(result.get('[data-test=connect-models]').findAll('[data-test=copy-value]').map((m) => m.text())).toEqual(['personal', 'bygglov'])
    expect(result.get('[data-test=connect-models]').text()).toContain(L.connect.notPublished)
    const example = result.get('[data-test=connect-example] [data-test=copy-value]').text()
    expect(example).toContain('curl https://rag.example.se/v1/chat/completions')
    expect(example).toContain('Authorization: Bearer ume-rag-sk-secret')
    expect(example).toContain('"model":"bygglov"')
    expect(result.find('[data-test=stack-lines]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('says the assistants are being read, not that there are none, until they arrive', async () => {
    let arrive!: (page: { items: AssistantSummary[]; total: number }) => void
    vi.spyOn(api.assistants, 'list').mockReturnValue(new Promise((resolve) => (arrive = resolve)))
    const wrapper = await open()
    await pick(wrapper, 'integration')
    await wrapper.get('#client-new-name').setValue('Webb')
    await submit(wrapper)
    const picker = wrapper.get('[data-test=assistant-picker]')
    expect(picker.text()).not.toContain(L.assistants.none)
    expect(picker.get('[role=status]').text()).toBe(COMMON.loading)
    arrive({ items: assistants, total: assistants.length })
    await flushPromises()
    expect(picker.find('[data-test=assistant-a1]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('ticks the assistant it was opened from, and shows a refused assistant by name on its step', async () => {
    vi.spyOn(api.clients, 'create').mockRejectedValue(new ApiError(400, 'POST', {
      status: 400, errors: { assistantIds: ['Bygglov: A client cannot show an assistant at level sensitive.'] },
      errorCodes: { assistantIds: [{ code: 'audience_above_ceiling', params: { level: 'sensitive', subject: 'Bygglov' } }] },
    }))
    const wrapper = await open('a1')
    await pick(wrapper, 'integration')
    await wrapper.get('#client-new-name').setValue('Webb')
    await submit(wrapper)
    expect(ticked(wrapper, 'a1').checked).toBe(true)
    await submit(wrapper)
    await submit(wrapper)

    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(3, 4, L.steps.assistants))
    expect(wrapper.get('[data-test=assistants-error]').text()).toMatch(/^Bygglov: An app cannot show an assistant/)
    wrapper.unmount()
  })
})
