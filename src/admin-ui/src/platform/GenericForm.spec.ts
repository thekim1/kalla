import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import type { GatewayModel } from '@/api/types'
import { LEVEL_LABEL, MODEL_AVAILABILITY, PLATFORM, SETTINGS_PAGE } from '@/utils/labels'
import GenericForm from './GenericForm.vue'
import { withSwitchCounts } from './settingsPage'
import type { FieldSpec } from './schema'

const fields: FieldSpec[] = [
  { name: 'name', label: 'name', kind: 'text', required: true, nullable: false },
  { name: 'allowedOrigins', label: 'allowedOrigins', kind: 'list', required: false, nullable: false },
  { name: 'value', label: 'value', kind: 'json', required: false, nullable: false },
]

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'RAG-XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT'
})

describe('GenericForm', () => {
  it('shows the nightly answer run as a switch over its count: off sends 0, on again starts at 100 (admin design §5.10)', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const evaluation = withSwitchCounts([{ name: 'evaluationAnswerQuestions', label: 'x', kind: 'number', required: false, nullable: false }], 'evaluation')
    expect(evaluation[0]).toMatchObject({ kind: 'switchCount', onValue: 100, switchLabel: SETTINGS_PAGE.answerRun.switch })
    const wrapper = mount(GenericForm, {
      props: { formId: 'f', fields: evaluation, initial: { evaluationAnswerQuestions: 40, version: '3' }, method: 'put', url: '/api/settings/evaluation', submitLabel: 'Save' },
    })
    const toggle = wrapper.get('[role=switch]')
    expect(toggle.attributes('aria-checked')).toBe('true')
    expect(toggle.attributes('aria-labelledby')).toBe('f-evaluationAnswerQuestions-switch-label')
    expect(wrapper.text()).toContain(SETTINGS_PAGE.answerRun.switch)
    expect((wrapper.get('#f-evaluationAnswerQuestions').element as HTMLInputElement).value).toBe('40')

    await toggle.trigger('click')
    expect(wrapper.find('#f-evaluationAnswerQuestions').exists()).toBe(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ evaluationAnswerQuestions: 0, version: '3' })

    await toggle.trigger('click')
    expect((wrapper.get('#f-evaluationAnswerQuestions').element as HTMLInputElement).value).toBe('100')
    await wrapper.get('#f-evaluationAnswerQuestions').setValue('5')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual({ evaluationAnswerQuestions: 5, version: '3' })
  })

  it('keeps the switch on while the count is edited: an empty count is required, 0 typed while on is not "off" (#984 review)', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const evaluation = withSwitchCounts([{ name: 'evaluationAnswerQuestions', label: 'x', kind: 'number', required: false, nullable: false }], 'evaluation')
    const wrapper = mount(GenericForm, {
      props: { formId: 'f', fields: evaluation, initial: { evaluationAnswerQuestions: 40, version: '3' }, method: 'put', url: '/api/settings/evaluation', submitLabel: 'Save' },
      attachTo: document.body,
    })
    await wrapper.get('#f-evaluationAnswerQuestions').setValue('')
    expect(wrapper.get('[role=switch]').attributes('aria-checked')).toBe('true')
    expect(wrapper.find('#f-evaluationAnswerQuestions').exists()).toBe(true)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(fetch).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain(PLATFORM.form.required)
    await wrapper.get('#f-evaluationAnswerQuestions').setValue('25')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ evaluationAnswerQuestions: 25, version: '3' })
    wrapper.unmount()

    // No stored value: off, and off sends 0 (never the server's default of 100).
    const empty = mount(GenericForm, { props: { formId: 'g', fields: evaluation, initial: {}, method: 'put', url: '/api/settings/evaluation', submitLabel: 'Save' } })
    expect(empty.get('[role=switch]').attributes('aria-checked')).toBe('false')
    await empty.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual({ evaluationAnswerQuestions: 0 })
  })

  it('sends the edited values with the version it read', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ id: 'c1' }))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(GenericForm, {
      props: { formId: 'f', fields, initial: { name: 'Open WebUI', allowedOrigins: ['https://a'], value: { x: 1 }, version: '7' }, method: 'put', url: '/api/clients/c1', submitLabel: 'Spara' },
    })
    await wrapper.get('#f-allowedOrigins').setValue('https://a\nhttps://b\n')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    const [url, init] = fetch.mock.calls[0]!
    expect(url).toBe('/api/clients/c1')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body)).toEqual({ name: 'Open WebUI', allowedOrigins: ['https://a', 'https://b'], value: { x: 1 }, version: '7' })
    expect(wrapper.emitted('saved')?.[0]).toEqual([{ id: 'c1' }])
  })

  it('stops at bad JSON and a missing required field, and shows the server field errors', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ status: 400, errors: { name: ['Namnet finns redan.'] } }, 400))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields, initial: {}, method: 'post', url: '/api/clients', submitLabel: 'Skapa' }, attachTo: document.body })
    await wrapper.get('#f-value').setValue('{nope')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.text()).toContain(PLATFORM.form.invalidJson)
    expect(wrapper.text()).toContain(PLATFORM.form.required)
    expect(fetch).not.toHaveBeenCalled()

    await wrapper.get('#f-value').setValue('')
    await wrapper.get('#f-name').setValue('Open WebUI')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('Namnet finns redan.')
    wrapper.unmount()
  })

  it('picks model fields from the mirror and sends a JSON member back inside its JSON field (#466)', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const models: GatewayModel[] = [
      { name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['onprem'], available: true },
      { name: 'claude', kind: 'model', modelKind: 'chat', residencies: ['eu'], available: false },
      { name: 'rerank-1', kind: 'model', modelKind: 'rerank', residencies: ['onprem'], available: null },
    ]
    const modelFields: FieldSpec[] = [
      { name: 'rerankModel', label: 'rerankModel', kind: 'model', modelKind: 'chat', required: false, nullable: true, parent: 'value' },
      { name: 'gatewayRerankModel', label: 'gatewayRerankModel', kind: 'model', modelKind: 'rerank', required: false, nullable: true, parent: 'value' },
      { name: 'value', label: 'value', kind: 'json', required: true, nullable: false },
    ]
    const initial = { value: { rerankModel: 'retired', gatewayRerankModel: 'rerank-1', topK: 8 }, version: '4' }
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields: modelFields, initial, method: 'put', url: '/api/settings/retrieval', submitLabel: 'Save', models } })

    const chatSelect = wrapper.get('#f-rerankModel').element as HTMLSelectElement
    expect(chatSelect.value).toBe('retired')
    expect([...chatSelect.options].map((o) => [o.value, o.text, o.disabled])).toEqual([
      ['', PLATFORM.form.none, false],
      ['retired', `retired — ${MODEL_AVAILABILITY.notListed}`, false],
      ['gpt-oss', 'gpt-oss', false],
      ['claude', `claude — ${MODEL_AVAILABILITY.notAvailable}`, true],
    ])
    const rerankSelect = wrapper.get('#f-gatewayRerankModel').element as HTMLSelectElement
    expect([...rerankSelect.options].map((o) => o.value)).toEqual(['', 'rerank-1'])
    expect(wrapper.text()).toContain(MODEL_AVAILABILITY.unknownOnSave)
    expect(JSON.parse((wrapper.get('#f-value').element as HTMLTextAreaElement).value)).toEqual({ topK: 8 })
    expect(wrapper.text()).toContain(PLATFORM.form.membersAbove)

    // Unchanged, the stale value goes back as it was; an emptied picker leaves its member out.
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ value: { topK: 8, rerankModel: 'retired', gatewayRerankModel: 'rerank-1' }, version: '4' })

    await wrapper.get('#f-rerankModel').setValue('gpt-oss')
    await wrapper.get('#f-gatewayRerankModel').setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual({ value: { topK: 8, rerankModel: 'gpt-oss' }, version: '4' })
  })

  it('edits a date-time field as a date and time input and sends it back as ISO (admin design §5.8)', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const field: FieldSpec = { name: 'expiresAt', label: 'expiresAt', kind: 'datetime', required: false, nullable: true }
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields: [field], initial: { expiresAt: '2026-12-31T12:00:00Z' }, method: 'put', url: '/api/x', submitLabel: 'Save' } })
    const input = wrapper.get('#f-expiresAt')
    expect(input.attributes('type')).toBe('datetime-local')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ expiresAt: '2026-12-31T12:00:00.000Z' })

    await input.setValue('2027-01-15T08:30')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual({ expiresAt: new Date('2027-01-15T08:30').toISOString() })

    await input.setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[2]![1].body)).toEqual({ expiresAt: null })
  })

  it('shows an information class by its name in a select, and sends its value (M-16 step 8)', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({ id: 'sp1' }))
    vi.stubGlobal('fetch', fetch)
    const level: FieldSpec = { name: 'level', label: 'Level', kind: 'select', options: ['public', 'internal', 'sensitive'], required: true, nullable: false }
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields: [level], initial: {}, method: 'post', url: '/api/site-profiles', submitLabel: 'Create' } })
    const select = wrapper.get('#f-level')
    expect([...(select.element as HTMLSelectElement).options].map((o) => [o.value, o.text])).toEqual([
      ['public', LEVEL_LABEL.public], ['internal', LEVEL_LABEL.internal], ['sensitive', LEVEL_LABEL.sensitive],
    ])
    await select.setValue('internal')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ level: 'internal' })
  })

  it('sends an emptied model property as null', async () => {
    document.cookie = 'RAG-XSRF-TOKEN=t'
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const models: GatewayModel[] = [{ name: 'gpt-oss', kind: 'model', modelKind: 'chat', residencies: ['onprem'], available: true }]
    const field: FieldSpec = { name: 'evaluationModel', label: 'evaluationModel', kind: 'model', modelKind: 'chat', required: false, nullable: true }
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields: [field], initial: { evaluationModel: 'gpt-oss' }, method: 'put', url: '/api/settings/evaluation', submitLabel: 'Save', models } })
    expect(wrapper.text()).not.toContain(MODEL_AVAILABILITY.unknownOnSave)
    await wrapper.get('#f-evaluationModel').setValue('')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({ evaluationModel: null })
  })

  it('keeps a number field short and a text field full width', () => {
    const number: FieldSpec = { name: 'auditRetentionDays', label: 'auditRetentionDays', kind: 'number', required: false, nullable: true }
    const wrapper = mount(GenericForm, { props: { formId: 'f', fields: [fields[0]!, number], initial: {}, method: 'put', url: '/api/settings/retention', submitLabel: 'Save' } })
    expect(wrapper.get('#f-auditRetentionDays').classes()).toContain('max-w-48')
    expect(wrapper.get('#f-name').classes()).not.toContain('max-w-48')
  })
})
