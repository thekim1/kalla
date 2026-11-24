import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { AnswerEvaluation, AnswerRunSummary } from '@/api/types'
import { ANSWER_EVALUATION as L } from '@/utils/labels'
import AnswerEvaluationCard from './AnswerEvaluationCard.vue'

const latest: AnswerRunSummary = {
  id: 'r2', version: 3, startedAt: '2026-11-23T03:00:00Z', finishedAt: '2026-11-23T03:20:00Z', questions: 40, found: 31, faithfulness: 0.875,
  citationPrecision: 0.9, wins: 2, losses: 1,
}
const previous: AnswerRunSummary = { ...latest, id: 'r1', startedAt: '2026-11-22T03:00:00Z', faithfulness: 0.8, citationPrecision: 0.95, wins: null, losses: null }
const evaluation = (over: Partial<AnswerEvaluation> = {}): AnswerEvaluation => ({
  enabled: true, questionsPerNight: 100, latest, previous, live: { days: 30, answered: 8, uncited: 2, rate: 0.25 }, ...over,
})

async function open(value: AnswerEvaluation) {
  vi.spyOn(api.assistantTests, 'answerEvaluation').mockResolvedValue(value)
  const wrapper = mount(AnswerEvaluationCard, { props: { assistantId: 'a1' } })
  await flushPromises()
  return wrapper
}

describe('the Quality tab\'s answer evaluation (admin design §5.5.4, B-58)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    setActivePinia(createPinia())
  })

  it('shows the last run\'s figures beside the run before, its counts and the live uncited rate', async () => {
    const wrapper = await open(evaluation())
    expect(api.assistantTests.answerEvaluation).toHaveBeenCalledWith('a1')
    expect(wrapper.get('[data-test=answer-evaluation-faithfulness]').text()).toMatch(/87[.,]5/)
    expect(wrapper.get('[data-test=answer-evaluation-precision]').text()).toMatch(/90/)
    expect(wrapper.text()).toMatch(/80.*%.*on/)
    expect(wrapper.get('[data-test=answer-evaluation-questions]').text()).toBe('40')
    expect(wrapper.get('[data-test=answer-evaluation-found]').text()).toBe('31')
    // Found is never greyed: 0 of N there is the worst result and needs attention (rule 8).
    expect(wrapper.get('[data-test=answer-evaluation-found]').classes()).not.toContain('text-fg-3')
    expect(wrapper.get('[data-test=answer-evaluation-changes]').text()).toBe(L.changes(2, 1))
    expect(wrapper.get('[data-test=answer-evaluation-uncited]').text()).toMatch(/25.*2 of 8 answers, last 30 days/)
    expect(wrapper.find('[data-test=answer-evaluation-off]').exists()).toBe(false)
  })

  it('says when the run is off and keeps the last figures; says when there is no run yet', async () => {
    const off = await open(evaluation({ enabled: false, questionsPerNight: 0 }))
    expect(off.get('[data-test=answer-evaluation-off]').text()).toBe(L.off)
    expect(off.find('[data-test=answer-evaluation-faithfulness]').exists()).toBe(true)

    vi.restoreAllMocks()
    const none = await open(evaluation({ latest: null, previous: null, live: { days: 30, answered: 0, uncited: 0, rate: null } }))
    expect(none.get('[data-test=answer-evaluation-none]').text()).toBe(L.noRun)
    expect(none.get('[data-test=answer-evaluation-uncited]').text()).toBe(L.uncitedNone(30))
  })
})
