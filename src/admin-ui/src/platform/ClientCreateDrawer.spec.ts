import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { api } from '@/api'
import type { ApiKey, Client } from '@/api/types'
import { PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientCreateDrawer from './ClientCreateDrawer.vue'

const L = PLATFORM_CLIENTS

async function open() {
  const wrapper = mount(ClientCreateDrawer, { props: { open: false }, attachTo: document.body })
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

describe('Add client (admin design §5.9)', () => {
  it('offers Open WebUI, integration and internal, and shows the later kinds disabled', async () => {
    const wrapper = await open()
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(1, 3, L.steps.kind))
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
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(3, 3, L.steps.firstKey))
    expect((wrapper.get('#client-new-key-name').element as HTMLInputElement).value).toBe(L.firstKey.defaultName('Pilot'))
    await submit(wrapper)

    expect(create).toHaveBeenCalledWith({
      name: 'Pilot', kind: 'open-webui', classificationCeiling: 'internal', storesHistory: true, clientRetentionDays: 90, oAuthClientId: null,
      portalUrl: 'https://chat.example.invalid', isEnabled: true, allowedOrigins: [], firstKey: { name: 'Pilot key' },
    })
    expect(wrapper.emitted('created')?.[0]).toEqual([client])
    const result = wrapper.get('[data-test=client-created]')
    expect(result.get('[data-test=secret]').text()).toContain('ume-rag-sk-secret')
    expect(result.get('[data-test=client-id]').text()).toContain('0199-client')
    const lines = result.get('[data-test=stack-lines]')
    expect(lines.get('[data-test=copy-value]').text()).toBe('UME_RAG_OPENWEBUI_API_KEY=ume-rag-sk-secret\nUME_RAG_OPENWEBUI_CLIENT_ID=0199-client')
    expect(lines.findAll('[data-test=copy]')).toHaveLength(1)
    expect(lines.text()).toContain(L.result.stackSentence)
    wrapper.unmount()
  })

  it('creates a client without a key when the first key is turned off, and shows no stack lines', async () => {
    const create = vi.spyOn(api.clients, 'create').mockResolvedValue({ client: { ...client, kind: 'internal', classificationCeiling: 'sensitive' }, key: null })
    const wrapper = await open()
    await pick(wrapper, 'internal')
    await wrapper.get('#client-new-name').setValue('Tools')
    await submit(wrapper)
    await wrapper.get('[data-test=create-key] input').setValue(false)
    await submit(wrapper)

    expect(create.mock.calls[0]![0]).toMatchObject({ kind: 'internal', classificationCeiling: 'sensitive', storesHistory: false, clientRetentionDays: null, firstKey: null })
    expect(wrapper.find('[data-test=secret]').exists()).toBe(false)
    expect(wrapper.find('[data-test=stack-lines]').exists()).toBe(false)
    expect(wrapper.text()).toContain(L.result.noKey)
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
    expect(wrapper.get('[data-test=step]').text()).toBe(L.step(2, 3, L.steps.fields))
    wrapper.unmount()
  })
})
