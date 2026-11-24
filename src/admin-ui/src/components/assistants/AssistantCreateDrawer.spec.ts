import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Assistant, AssistantTemplate, Department } from '@/api/types'
import { ASSISTANTS } from '@/utils/labels'
import AssistantCreateDrawer from './AssistantCreateDrawer.vue'

const departments: Department[] = [{ id: 'd1', name: 'Bygg', costCenterCode: '1000' } as Department]

const template = (id: string, name: string, isActive = true): AssistantTemplate => ({
  id, name, description: `${name}: beskrivning.`, systemPrompt: 'Svara kort.', notFound: {}, querySteps: {}, isActive,
  updatedAt: '2026-11-22T10:00:00Z', updatedBy: null, version: '1',
})

const created = { id: 'a1', name: 'Policyhjälpen' } as Assistant

async function open() {
  const wrapper = mount(AssistantCreateDrawer, { props: { open: false, departments }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

/** The Start from cards (admin design §5.12, Drawers). */
const cards = () => document.body.querySelector<HTMLFieldSetElement>('#assistant-template')
const radios = () => [...document.body.querySelectorAll<HTMLInputElement>('input[name=assistant-template]')]
function choose(value: string): void {
  const radio = radios().find((r) => r.value === value)!
  radio.checked = true
  radio.dispatchEvent(new Event('change'))
}

async function fillAndSave() {
  const name = document.body.querySelector<HTMLInputElement>('#assistant-name')!
  name.value = 'Policyhjälpen'
  name.dispatchEvent(new Event('input'))
  const level = document.body.querySelector<HTMLInputElement>('#assistant-level-internal')!
  level.checked = true
  level.dispatchEvent(new Event('change'))
  document.body.querySelector<HTMLFormElement>('#assistant-create')!.dispatchEvent(new Event('submit'))
  await flushPromises()
}

describe('Create assistant: the template (admin design §5 step 9, B-54)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
  })

  it('offers the active templates with Allmän rådgivning preselected, and sends the chosen one', async () => {
    vi.spyOn(api.assistantTemplates, 'list').mockResolvedValue([
      template('t1', 'Handläggningsstöd'), template('t2', 'Allmän rådgivning'), template('t3', 'Intern policy'), template('t4', 'Gammal', false),
    ])
    const create = vi.spyOn(api.assistants, 'create').mockResolvedValue(created)
    const wrapper = await open()

    expect(cards()!.tagName).toBe('FIELDSET')
    expect(cards()!.querySelector('legend')!.textContent).toBe(ASSISTANTS.template)
    const labels = [...cards()!.querySelectorAll('label')].map((l) => l.querySelector('.font-medium')!.textContent)
    expect(labels).toEqual(['Handläggningsstöd', 'Allmän rådgivning', 'Intern policy', ASSISTANTS.templateNone])
    expect(radios().find((r) => r.checked)!.value).toBe('t2')
    // One line each: the template's description.
    expect(document.body.querySelector('[data-test=start-from-t2] [data-test=start-from-line]')!.textContent).toBe('Allmän rådgivning: beskrivning.')
    expect(cards()!.getAttribute('aria-describedby')).toBe('assistant-template-hint')
    // The department select asks for a choice.
    expect(document.body.querySelector('#assistant-department option[disabled]')?.textContent).toBe(ASSISTANTS.chooseDepartment)

    choose('t3')
    await fillAndSave()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ name: 'Policyhjälpen', departmentId: 'd1', classification: 'internal', templateId: 't3' }))
    expect(wrapper.emitted('created')?.[0]?.[0]).toEqual(created)
    wrapper.unmount()
  })

  it('sends no template when the field is emptied, so the draft starts from the default instructions', async () => {
    vi.spyOn(api.assistantTemplates, 'list').mockResolvedValue([template('t2', 'Allmän rådgivning')])
    const create = vi.spyOn(api.assistants, 'create').mockResolvedValue(created)
    const wrapper = await open()
    choose('')
    await fillAndSave()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ templateId: null }))
    wrapper.unmount()
  })

  it('reads out a server error on the template with the Start from group, and focuses the group', async () => {
    vi.spyOn(api.assistantTemplates, 'list').mockResolvedValue([template('t2', 'Allmän rådgivning')])
    vi.spyOn(api.assistants, 'create').mockRejectedValue(new ApiError(400, 'POST', { errors: { templateId: ['The template is no longer active.'] } }))
    const wrapper = await open()
    await fillAndSave()
    const group = cards()!
    expect(group.getAttribute('aria-describedby')).toBe('assistant-template-error assistant-template-hint')
    expect(document.getElementById('assistant-template-error')!.textContent).toContain('no longer active')
    expect(document.activeElement).toBe(group)
    wrapper.unmount()
  })

  it('works without templates: no field, and no template is sent', async () => {
    vi.spyOn(api.assistantTemplates, 'list').mockRejectedValue(new Error('offline'))
    const create = vi.spyOn(api.assistants, 'create').mockResolvedValue(created)
    const wrapper = await open()
    expect(cards()).toBeNull()
    await fillAndSave()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ templateId: null }))
    wrapper.unmount()
  })
})
