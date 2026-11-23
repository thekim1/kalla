import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Assistant, AssistantCopyResult, Department } from '@/api/types'
import { ASSISTANT } from '@/utils/labels'
import AssistantCopyDrawer from './AssistantCopyDrawer.vue'

const assistant = {
  id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', departmentId: 'd1', classification: 'internal', version: 'v1',
} as Assistant

const departments = [{ id: 'd1', name: 'Bygg' }, { id: 'd2', name: 'Miljö' }] as Department[]
const result: AssistantCopyResult = { assistant: { ...assistant, id: 'a2', name: 'Miljöassistenten', departmentId: 'd2' }, warnings: [], leftOut: { links: 0, expectedDocuments: 0 } }

async function open() {
  const wrapper = mount(AssistantCopyDrawer, { props: { open: false, assistant }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

const input = (id: string) => document.body.querySelector<HTMLInputElement & HTMLSelectElement>(`#${id}`)!
const submit = async () => {
  document.body.querySelector<HTMLFormElement>('#assistant-copy')!.dispatchEvent(new Event('submit'))
  await flushPromises()
}

describe('Copy an assistant (admin design §4.5, B-55)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    vi.spyOn(api.organisation, 'departments').mockResolvedValue(departments)
  })

  it('starts from the name with "(copy)" in the same department, and sends the chosen name and department', async () => {
    const copy = vi.spyOn(api.assistants, 'copy').mockResolvedValue(result)
    const wrapper = await open()
    expect(input('assistant-copy-name').value).toBe(ASSISTANT.copyName('Bygglovsassistenten'))
    expect(input('assistant-copy-department').value).toBe('d1')

    input('assistant-copy-name').value = 'Miljöassistenten'
    input('assistant-copy-name').dispatchEvent(new Event('input'))
    input('assistant-copy-department').value = 'd2'
    input('assistant-copy-department').dispatchEvent(new Event('change'))
    await submit()
    expect(copy).toHaveBeenCalledWith('a1', { name: 'Miljöassistenten', departmentId: 'd2' })
    expect(wrapper.emitted('copied')?.[0]?.[0]).toEqual(result)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })

  it('shows a taken name on the name field and stays open', async () => {
    vi.spyOn(api.assistants, 'copy').mockRejectedValue(new ApiError(400, 'POST', {
      status: 400, errors: { slug: ['Something with the same name or key already exists.'] }, errorCodes: { slug: [{ code: 'already_exists' }] },
    }))
    const wrapper = await open()
    await submit()
    expect(wrapper.emitted('copied')).toBeUndefined()
    expect(document.body.textContent).toContain('already exists')
    wrapper.unmount()
  })

  it('says what a copy into another department left out, and nothing when it left out nothing (#983)', () => {
    expect(ASSISTANT.copyLeftOut(0, 0)).toBe('')
    expect(ASSISTANT.copyLeftOut(1, 0)).toBe('1 knowledge base not shared with its department was left out.')
    expect(ASSISTANT.copyLeftOut(2, 3)).toBe('2 knowledge bases not shared with its department were left out. 3 test questions lost their expected document.')
  })
})
