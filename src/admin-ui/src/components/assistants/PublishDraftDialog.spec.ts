import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import { ERROR_MESSAGES } from '@/utils/messages'
import type { AssistantDraft, RuleWarning, TestQuestion, VersionDiff } from '@/api/types'
import PublishDraftDialog from './PublishDraftDialog.vue'

const noText = { changed: false, tooLong: false, lines: [] }
const diff: VersionDiff = {
  from: 3, to: 4, toDraft: true, changed: true, settings: [{ name: 'model', from: 'fors/chat-eu', to: 'fors/chat' }], collectionsAdded: [], collectionsRemoved: [],
  topicsChanged: [], notFoundSv: noText, notFoundEn: noText,
  instructions: { changed: true, tooLong: false, lines: [{ op: 'insert', text: 'Svara kort och vänligt.' }] },
}
const contactMissing: RuleWarning = { code: 'assistant_contact_missing', params: null, affected: [] }
const systemPrompt: RuleWarning = { code: 'gateway_key_system_prompt', params: { key: 'Pilot' }, affected: ['k1'] }
const noKey: RuleWarning = { code: 'gateway_key_required', params: null, affected: [] }
const draftWith = (warnings: RuleWarning[]) => ({ warnings }) as unknown as AssistantDraft

const $ = <T extends HTMLElement = HTMLElement>(selector: string) => document.body.querySelector<T>(selector)

function open(props: Partial<InstanceType<typeof PublishDraftDialog>['$props']> = {}) {
  const action = vi.fn().mockResolvedValue(undefined)
  const wrapper = mount(PublishDraftDialog, {
    props: { open: true, assistantId: 'a1', publishedVersion: 3, warnings: [], needsGeneralKnowledgeConfirmation: false, action, ...props },
    attachTo: document.body,
  })
  return { wrapper, action }
}

beforeEach(() => {
  setActivePinia(createPinia())
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
  vi.spyOn(api.assistants, 'diff').mockResolvedValue(diff)
  vi.spyOn(api.assistantTests, 'questions').mockResolvedValue([{ id: 't1' }, { id: 't2' }, { id: 't3' }] as TestQuestion[])
  // By default the dialog's own read of the draft fails, so the page's copy (the warnings prop) stands.
  vi.spyOn(api.assistants, 'draft').mockRejectedValue(new Error('offline'))
})
afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('Publish draft\'s dialog (admin design §5.5.4, B-82)', () => {
  it('shows what changes against the published version, the test run\'s model calls, and publishes', async () => {
    const { wrapper, action } = open()
    await flushPromises()
    expect(api.assistants.diff).toHaveBeenCalledWith('a1', 3, 'draft')
    expect($('[data-test=publish-diff]')!.textContent).toContain('What changes against version 3')
    expect($('[data-test=version-diff] ins')!.textContent).toContain('Svara kort och vänligt.')
    expect($('[data-test=publish-cost]')!.textContent).toBe(('Publishing runs the test questions. 3 questions: about 6 model calls (one answer and one judgement each).'))
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect(action).toHaveBeenCalledWith(false)
    expect(wrapper.emitted('update:open')).toEqual([[false]])
    wrapper.unmount()
  })

  it('shows no diff on a first publish, only what it does', async () => {
    const { wrapper } = open({ publishedVersion: null })
    await flushPromises()
    expect(api.assistants.diff).not.toHaveBeenCalled()
    expect($('[data-test=publish-diff]')).toBeNull()
    expect($('[data-test=publish-first]')!.textContent).toContain('the draft becomes version 1')
    wrapper.unmount()
  })

  it('blocks Publish on a refusal and lists it; a gateway key note does not block', async () => {
    const blocked = open({ warnings: [contactMissing, systemPrompt] })
    await flushPromises()
    expect($('[data-test=publish-warnings] h3')!.textContent).toBe('Fix these before you publish')
    expect([...document.body.querySelectorAll('[data-test=publish-refusal]')]).toHaveLength(1)
    expect([...document.body.querySelectorAll('[data-test=publish-note]')]).toHaveLength(1)
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBe('true')
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect(blocked.action).not.toHaveBeenCalled()
    blocked.wrapper.unmount()

    const noted = open({ warnings: [systemPrompt] })
    await flushPromises()
    expect($('[data-test=publish-warnings] h3')!.textContent).toBe('Worth knowing')
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBeNull()
    noted.wrapper.unmount()
  })

  it('asks V36\'s confirmation in the same dialog, and sends it once ticked', async () => {
    const { wrapper, action } = open({ needsGeneralKnowledgeConfirmation: true, warnings: [{ code: 'general_knowledge_public_unconfirmed', params: null, affected: [] }] })
    await flushPromises()
    expect($('[data-test=publish-warnings]')).toBeNull() // V36 is the checkbox, not a listed warning
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBe('true')
    $<HTMLInputElement>('[data-test=publish-confirm-general-knowledge] input')!.click()
    await flushPromises()
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect(action).toHaveBeenCalledWith(true)
    wrapper.unmount()
  })

  it('shows a refusal the server still answers in the dialog and stays open', async () => {
    const { wrapper, action } = open()
    action.mockRejectedValue(new ApiError(409, 'POST', { status: 409, code: 'assistant_contact_missing' }))
    await flushPromises()
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect(document.body.textContent).toContain(ERROR_MESSAGES.assistant_contact_missing)
    expect(wrapper.emitted('update:open')).toBeUndefined()
    wrapper.unmount()
  })

  it('reads the draft\'s warnings again on opening, so a fix made since the last load no longer blocks, and waits for that read', async () => {
    let answer: (d: AssistantDraft) => void = () => undefined
    vi.mocked(api.assistants.draft).mockReturnValue(new Promise((resolve) => (answer = resolve)))
    const { wrapper } = open({ warnings: [contactMissing] })
    await flushPromises()
    expect(api.assistants.draft).toHaveBeenCalledWith('a1')
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBe('true')
    answer(draftWith([]))
    await flushPromises()
    expect($('[data-test=publish-refusal]')).toBeNull()
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBeNull()
    wrapper.unmount()
  })

  it('takes the missing gateway key for a refusal when the publish check raises it too (the gateway acts as keys), else for a note', async () => {
    vi.mocked(api.assistants.draft).mockResolvedValue(draftWith([noKey, noKey]))
    const asKeys = open()
    await flushPromises()
    expect([...document.body.querySelectorAll('[data-test=publish-refusal]')]).toHaveLength(1)
    expect($('[data-test=publish-note]')).toBeNull()
    expect($('[data-test=publish-confirm]')!.getAttribute('aria-disabled')).toBe('true')
    asKeys.wrapper.unmount()

    vi.mocked(api.assistants.draft).mockResolvedValue(draftWith([noKey]))
    const note = open()
    await flushPromises()
    expect($('[data-test=publish-refusal]')).toBeNull()
    expect([...document.body.querySelectorAll('[data-test=publish-note]')]).toHaveLength(1)
    note.wrapper.unmount()
  })

  it('offers V36\'s checkbox when the server asks for it unexpectedly, and sends it on the next try', async () => {
    const { wrapper, action } = open()
    action.mockRejectedValueOnce(new ApiError(409, 'POST', { status: 409, code: 'general_knowledge_public_unconfirmed' }))
    await flushPromises()
    expect($('[data-test=publish-confirm-general-knowledge]')).toBeNull()
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect($('[data-test=publish-confirm-general-knowledge]')).not.toBeNull()
    $<HTMLInputElement>('[data-test=publish-confirm-general-knowledge] input')!.click()
    await flushPromises()
    $('[data-test=publish-confirm]')!.click()
    await flushPromises()
    expect(action).toHaveBeenLastCalledWith(true)
    wrapper.unmount()
  })
})
