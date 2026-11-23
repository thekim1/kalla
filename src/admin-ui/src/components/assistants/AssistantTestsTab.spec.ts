import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Assistant, TestQuestion, TestRunReport, TestRunSummary } from '@/api/types'
import { ASSISTANT_TESTS as L } from '@/utils/labels'
import AssistantTestsTab from './AssistantTestsTab.vue'

const assistant = { id: 'a1', name: 'Kompostguiden', publishedVersion: 2, draft: null, version: 'a5' } as unknown as Assistant
const questions: TestQuestion[] = [
  { id: 'q1', ordinal: 0, question: 'Hur komposterar jag?', expectedBehaviour: 'Hänvisar till kompostsidan.', expectedDocument: { id: 'd1', title: 'Kompostering', deleted: false, unlinked: false },
    expectedPhrase: 'kompost', updatedAt: '', updatedBy: null, version: '7' },
  { id: 'q2', ordinal: 1, question: 'Vem hjälper till?', expectedBehaviour: 'Hänvisar till kontaktcenter.', expectedDocument: { id: 'd9', title: null, deleted: true, unlinked: true },
    expectedPhrase: null, updatedAt: '', updatedBy: null, version: '8' },
]
const run: TestRunSummary = {
  id: 'r2', version: 2, startedAt: '2026-11-23T10:00:00Z', finishedAt: '2026-11-23T10:01:00Z', questions: 2, asExpected: 1, better: 0, worse: 1,
  comparedRunId: 'r1', comparedVersion: 1, comparedAsExpected: 2,
}
const report: TestRunReport = {
  run,
  results: [
    { questionId: 'q1', question: 'Hur komposterar jag?', answer: 'Testsvar. Se [1].', checks: true, verdict: 'pass', judgeNote: 'Bra.', asExpected: true, change: null,
      expectedDocument: null, expectedPhrase: 'kompost', expectedBehaviour: '' },
    { questionId: 'q2', question: 'Vem hjälper till?', answer: 'Vet inte.', checks: null, verdict: 'fail', judgeNote: 'Nämner inte kontaktcenter.', asExpected: false,
      change: 'worse', expectedDocument: null, expectedPhrase: null, expectedBehaviour: '' },
  ],
}

async function open(canEdit = true) {
  const wrapper = mount(AssistantTestsTab, { props: { assistant, canEdit, collections: [{ id: 'c1', name: 'Kompost' }] }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

describe('Test questions (admin design §7.4, B-57)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    setActivePinia(createPinia())
    vi.spyOn(api.assistantTests, 'questions').mockResolvedValue(questions)
    vi.spyOn(api.assistantTests, 'runs').mockResolvedValue([run])
    vi.spyOn(api.assistantTests, 'report').mockResolvedValue(report)
  })

  it('shows the set with its marks and the report card with the counts, the changed questions and the judge\'s reasons', async () => {
    const wrapper = await open()
    expect(wrapper.get('[data-test=test-summary]').text()).toBe(`${L.summary(2, 1)} ${L.previous(1, 2)} · ${L.changes(1, 0)}`)
    expect(wrapper.get('[data-test=test-changed]').text()).toContain('Nämner inte kontaktcenter.')
    expect(wrapper.get('[data-test=test-changed]').text()).toContain(L.worse)
    expect(wrapper.get('[data-test=test-question-q2]').text()).toContain(L.documentDeleted)
    expect(wrapper.get('[data-test=test-result-q1]').text()).toContain(L.checks.true)
    expect(wrapper.get('[data-test=test-cost]').text()).toBe(L.cost(2))
    wrapper.unmount()
  })

  it('runs the questions on the chosen version and keeps or restores after a publish', async () => {
    const start = vi.spyOn(api.assistantTests, 'run').mockResolvedValue({ ...run, id: 'r3', finishedAt: null })
    const restore = vi.spyOn(api.assistants, 'restore').mockResolvedValue({ ...assistant, publishedVersion: 3 } as Assistant)
    const wrapper = await open()

    await wrapper.get('[data-test=test-run-start]').trigger('click')
    await flushPromises()
    expect(start).toHaveBeenCalledWith('a1', 'Published')

    await wrapper.get('[data-test=test-restore]').trigger('click')
    await flushPromises()
    const confirm = [...document.body.querySelectorAll<HTMLButtonElement>('[data-test=confirm]')].at(-1)!
    confirm.click()
    await flushPromises()
    expect(restore).toHaveBeenCalledWith('a1', 1, 'a5', false)
    expect(wrapper.emitted('restored')?.[0]?.[0]).toMatchObject({ publishedVersion: 3 })

    await wrapper.get('[data-test=test-keep]').trigger('click')
    expect(wrapper.find('[data-test=test-decide]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows a read-only set without the run, add and edit controls', async () => {
    const wrapper = await open(false)
    expect(wrapper.find('[data-test=test-run]').exists()).toBe(false)
    expect(wrapper.find('[data-test=test-question-add]').exists()).toBe(false)
    expect(wrapper.find('[data-test=test-decide]').exists()).toBe(false)
    wrapper.unmount()
  })
})
