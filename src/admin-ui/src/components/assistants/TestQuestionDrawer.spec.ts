import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { DocumentSummary, TestQuestion } from '@/api/types'
import { ASSISTANT_TESTS as L } from '@/utils/labels'
import TestQuestionDrawer from './TestQuestionDrawer.vue'

const saved = { id: 'q1', version: '2' } as TestQuestion

async function open(props: Record<string, unknown> = {}) {
  const wrapper = mount(TestQuestionDrawer, {
    props: { open: false, assistantId: 'a1', question: null, collections: [{ id: 'c1', name: 'Kompost' }], ...props }, attachTo: document.body,
  })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

const field = (id: string) => document.body.querySelector<HTMLInputElement & HTMLTextAreaElement>(`#${id}`)!
const type = (id: string, value: string) => {
  field(id).value = value
  field(id).dispatchEvent(new Event('input'))
}
const submit = async () => {
  document.body.querySelector<HTMLFormElement>('#test-question-form')!.dispatchEvent(new Event('submit'))
  await flushPromises()
}

describe('A test question (admin design §7.4, V33)', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('starts from a question from the test panel and saves it with a document picked by title and a phrase', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.documents, 'list').mockResolvedValue({ items: [{ id: 'd1', title: 'Kompostering', displayName: null, url: null, live: true } as DocumentSummary], next: null })
    const create = vi.spyOn(api.assistantTests, 'create').mockResolvedValue(saved)
    const wrapper = await open({ initialQuestion: 'Hur komposterar jag?' })
    expect(field('test-question').value).toBe('Hur komposterar jag?')

    type('test-question-behaviour', ' Hänvisar till kompostsidan. ')
    type('test-question-search', 'kompost')
    await vi.advanceTimersByTimeAsync(300)
    vi.useRealTimers()
    await flushPromises()
    expect(api.documents.list).toHaveBeenCalledWith({ collectionId: 'c1', q: 'kompost', limit: 10 })
    document.body.querySelector<HTMLButtonElement>(`[aria-label="${L.pick('Kompostering')}"]`)!.click()
    await flushPromises()
    type('test-question-phrase', 'kompost')
    await submit()
    expect(create).toHaveBeenCalledWith('a1', {
      question: 'Hur komposterar jag?', expectedBehaviour: 'Hänvisar till kompostsidan.', expectedDocumentId: 'd1', expectedPhrase: 'kompost',
    })
    expect(wrapper.emitted('saved')?.[0]?.[0]).toEqual(saved)
    wrapper.unmount()
  })

  it('asks for the question and the expected behaviour before saving', async () => {
    const create = vi.spyOn(api.assistantTests, 'create')
    const wrapper = await open()
    await submit()
    expect(create).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain(L.questionRequired)
    expect(document.body.textContent).toContain(L.behaviourRequired)
    wrapper.unmount()
  })

  it('edits a question with its version', async () => {
    const update = vi.spyOn(api.assistantTests, 'update').mockResolvedValue(saved)
    const question = { id: 'q1', question: 'Fråga?', expectedBehaviour: 'Svarar.', expectedDocument: null, expectedPhrase: null, version: '7' } as TestQuestion
    const wrapper = await open({ question })
    type('test-question-behaviour', 'Svarar kort.')
    await submit()
    expect(update).toHaveBeenCalledWith('a1', 'q1', { question: 'Fråga?', expectedBehaviour: 'Svarar kort.', expectedDocumentId: null, expectedPhrase: null, version: '7' })
    wrapper.unmount()
  })
})
