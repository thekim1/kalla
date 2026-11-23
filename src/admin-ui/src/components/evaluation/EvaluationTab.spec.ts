import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { EvaluationResult, EvaluationRun } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { bffUser } from '@/test/fixtures'
import { EVALUATION } from '@/utils/labels'
import EvaluationTab from './EvaluationTab.vue'

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}
const dialogButton = (label: string) => [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === label).at(-1)!

function run(overrides: Partial<EvaluationRun>): EvaluationRun {
  return {
    id: 'r2', kind: 'publish-report', variant: 'hybrid', questionCount: 4, k: 10, found: 2, recallAtK: 0.5, mrr: 0.4, wins: 0, losses: 1, comparedRunId: 'r1',
    candidate: false, thresholdProposal: null, startedAt: '2026-10-30T10:02:00Z', finishedAt: '2026-10-30T10:05:00Z', documentGone: null, ...overrides,
  }
}
function result(questionId: string, layer: string, rank: number | null, answerable = true): EvaluationResult {
  return {
    questionId, question: `Fråga ${questionId}`, layer, rank, previousRank: null, hit: answerable ? rank != null : null, coverage: rank ? 'ok' : 'low',
    bestDistance: 0.3, bestKeywordScore: null,
  }
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('Test questions and quality (admin design §7.1, §7.2)', () => {
  it('leads the report with the human layer, compares with the earlier run and offers the threshold proposal', async () => {
    signIn(['rag-editor'])
    const latest = run({
      thresholdProposal: {
        answerable: 2, unanswerable: 1, falseNotFound: 1, unanswerableNotFound: 1,
        coverageMaxDistance: { value: 0.42, misclassified: 1 }, coverageMinKeywordScore: null,
      },
    })
    const earlier = run({ id: 'r1', comparedRunId: null, startedAt: '2026-10-29T10:00:00Z' })
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true,
      questions: [
        { layer: 'human', state: 'active', count: 3 },
        { layer: 'generated', state: 'active', count: 2 },
        { layer: 'generated', state: 'stale', count: 1 },
      ],
      latestRuns: [latest, earlier],
    })
    vi.spyOn(api.evaluation, 'results').mockImplementation(async (id) =>
      id === 'r2'
        ? [result('h1', 'human', 1), result('h2', 'human', null), result('h3', 'human', null, false), result('g1', 'generated', 2), result('g2', 'generated', null)]
        : [result('h1', 'human', 1), result('h2', 'human', 3), result('h3', 'human', null, false), result('g1', 'generated', 1), result('g2', 'generated', 4)],
    )
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: true } })
    await flushPromises()

    expect(wrapper.get('[data-test=layers]').text()).toContain('From your organisation3 active')
    expect(wrapper.get('[data-test=layers]').text()).toContain('2 active · 1 out of date')
    const human = wrapper.get('[data-test=human]').text()
    expect(human).toContain('Test questions from your organisation · 3 questions')
    expect(human).toContain('1,00 → 0,50') // recall@10 over the two answerable questions, then and now
    expect(human).toMatch(/Wrongly “found nothing”\s*50\s*%/)
    expect(human).toMatch(/answered “found nothing”\s*100\s*%/)
    expect(wrapper.get('[data-test=generated]').text()).toContain('1,00 → 0,50')
    expect(wrapper.get('[data-test=summary]').text()).toBe('Too few questions to compare.')
    expect(wrapper.get('[data-test=worse]').text()).toContain('Fråga h2')
    expect(wrapper.get('[data-test=proposal]').text()).toContain('Largest distance: 0,42 (1 question ends up wrong)')
    expect(wrapper.find('[data-test=history]').exists()).toBe(true)

    await wrapper.get('[data-test=use-proposal]').trigger('click')
    expect(wrapper.emitted('use-proposal')).toEqual([[{ coverageMaxDistance: 0.42, coverageMinKeywordScore: undefined }]])
  })

  it('runs the evaluation, polls the job and reloads; shows the generation forecast before generating', async () => {
    vi.useFakeTimers()
    signIn(['rag-editor'])
    const overview = vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: true, questions: [{ layer: 'human', state: 'active', count: 3 }], latestRuns: [] })
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })
    const start = vi.spyOn(api.evaluation, 'run').mockResolvedValue({ jobId: 'j1', alreadyQueued: false })
    vi.spyOn(api.jobs, 'get').mockResolvedValue({
      id: 'j1', class: 'eval', kind: 'on-demand', state: 'succeeded', enqueuedAt: '2026-11-02T10:00:00Z', completedAt: '2026-11-02T10:01:00Z', runId: null, result: null, lastError: null,
    })
    vi.spyOn(api.evaluation, 'forecast').mockResolvedValue({
      kind: 'generate-set', model: 'eval-model', judgeModel: 'eval-model', liveDocuments: 40, eligibleChunks: 300, activeQuestions: 0, target: 100, needed: 100,
      modelCalls: 320, estimatedTokens: 250000, estimatedCostUsd: 0.75,
    })
    const generate = vi.spyOn(api.evaluation, 'generate').mockResolvedValue({ jobId: 'j2', alreadyQueued: false, forecast: {} as never })

    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: true }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.text()).toContain('No evaluation has been run yet.')

    await wrapper.get('[data-test=run]').trigger('click')
    await flushPromises()
    expect(start).toHaveBeenCalledWith('c1')
    expect(overview).toHaveBeenCalledTimes(2)

    await wrapper.get('[data-test=generate]').trigger('click')
    await flushPromises()
    const forecast = document.body.querySelector('[data-test=forecast]')!.textContent!
    expect(forecast).toContain('100 new questions are needed to reach 100')
    expect(forecast).toContain('320 calls')
    expect(forecast).toContain('Model: eval-model')
    dialogButton('Create test questions').click()
    await flushPromises()
    expect(generate).toHaveBeenCalledWith('c1')
    wrapper.unmount()
    vi.useRealTimers()
  })

  it('adds a human question with an expected document picked by title, and retires one', async () => {
    vi.useFakeTimers()
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: true, questions: [], latestRuns: [] })
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({
      items: [{ id: 'q1', layer: 'human', state: 'active', question: 'Behöver jag bygglov för altan?', expectedAnswer: null, expectedDocumentIds: ['d1'], origin: null, createdBy: 'anna', retiredAt: null }],
      next: null,
    })
    vi.spyOn(api.documents, 'list').mockResolvedValue({
      items: [{
        id: 'd2', sourceId: 's1', title: 'Attefallshus', url: 'https://www.umea.se/attefall', displayName: null, mediaType: 'text/html', parseOutcome: 'ok',
        piiFlagged: false, piiCleared: false, piiHeld: false, held: false, live: true, sourceModifiedAt: null, fetchedAt: null, lastSeenAt: '2026-11-01T00:00:00Z',
      }],
      next: null,
    })
    const add = vi.spyOn(api.evaluation, 'addQuestion').mockResolvedValue({ jobId: 'j', questionId: 'q2' })
    const retire = vi.spyOn(api.evaluation, 'retireQuestion').mockResolvedValue({ jobId: 'j', questionId: 'q1' })

    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: true }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.text()).toContain('Behöver jag bygglov för altan?')
    expect(wrapper.text()).toContain('1 document')

    await wrapper.get('[data-test=add-question]').trigger('click')
    await flushPromises()
    const question = document.getElementById('eval-question') as HTMLInputElement
    question.value = 'Hur stort får ett attefallshus vara?'
    question.dispatchEvent(new Event('input'))
    const search = document.getElementById('eval-document-search') as HTMLInputElement
    search.value = 'attefall'
    search.dispatchEvent(new Event('input'))
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    ;(document.body.querySelector('[aria-label="Add Attefallshus"]') as HTMLButtonElement).click()
    await flushPromises()
    ;(document.body.querySelector('[data-test=save-question]') as HTMLButtonElement).click()
    await flushPromises()
    expect(add).toHaveBeenCalledWith('c1', { question: 'Hur stort får ett attefallshus vara?', expectedAnswer: null, expectedDocumentIds: ['d2'] })

    await wrapper.get('[data-test=retire-question]').trigger('click')
    await flushPromises()
    dialogButton('Remove').click()
    await flushPromises()
    expect(retire).toHaveBeenCalledWith('c1', 'q1')
    wrapper.unmount()
    vi.useRealTimers()
  })

  it('shows no editing buttons to a reader', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: true, questions: [], latestRuns: [] })
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })
    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: false } })
    await flushPromises()
    expect(wrapper.find('[data-test=run]').exists()).toBe(false)
    expect(wrapper.find('[data-test=generate]').exists()).toBe(false)
    expect(wrapper.find('[data-test=add-question]').exists()).toBe(false)
  })

  it('shows questions whose expected document was deleted, and the run’s count of them (processing design §13.3)', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true,
      questions: [{ layer: 'human', state: 'active', count: 2 }, { layer: 'human', state: 'stale', count: 1 }],
      latestRuns: [run({ documentGone: 2 })],
    })
    vi.spyOn(api.evaluation, 'results').mockResolvedValue([result('h1', 'human', 1)])
    const stale = { id: 'q1', layer: 'human', state: 'stale', question: 'Var finns blanketten?', expectedAnswer: null, expectedDocumentIds: ['gone', 'd1'], origin: null, createdBy: 'anna', retiredAt: null }
    const retiredGenerated = { ...stale, id: 'q2', layer: 'generated', question: 'Genererad fråga?', retiredAt: '2026-11-02T00:00:00Z' }
    const questions = vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [stale, retiredGenerated], next: null })
    vi.spyOn(api.documents, 'get').mockImplementation(async (id) => {
      if (id === 'gone') throw new ApiError(404, 'GET', null)
      return { id: 'd1', title: 'Blanketter', displayName: null, url: null } as never
    })
    const update = vi.spyOn(api.evaluation, 'updateQuestion').mockResolvedValue({ jobId: 'j', questionId: 'q1' })

    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: true }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.get('[data-test=document-gone]').text()).toBe('2 questions left out: their documents were deleted')
    expect(wrapper.get('[data-test=deleted-notice]').text()).toContain('1 question expects a document that was deleted.')
    await wrapper.get('[data-test=show-deleted]').trigger('click')
    await flushPromises()
    expect(questions).toHaveBeenLastCalledWith('c1', expect.objectContaining({ layer: 'human', state: 'stale' }))
    expect(wrapper.find('[data-test=deleted-notice]').exists()).toBe(false)
    // Only the human question says so; a generated stale question is retired at its top-up.
    expect(wrapper.findAll('[data-test=document-deleted]').map((e) => e.text())).toEqual(['Its expected document was deleted'])
    expect(wrapper.findAll('[data-test=retire-question]')).toHaveLength(1)

    // Re-pointing: the deleted document is marked and has to go before the question is saved.
    await wrapper.findAll('[data-test=edit-question]')[0]!.trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=deleted-document]')!.textContent).toBe('Deleted document')
    ;(document.body.querySelector('[data-test=save-question]') as HTMLButtonElement).click()
    await flushPromises()
    expect(update).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('Remove the deleted document and choose another one.')
    ;(document.body.querySelector('[aria-label="Remove Deleted document"]') as HTMLButtonElement).click()
    await flushPromises()
    ;(document.body.querySelector('[data-test=save-question]') as HTMLButtonElement).click()
    await flushPromises()
    expect(update).toHaveBeenCalledWith('c1', 'q1', { question: 'Var finns blanketten?', expectedAnswer: null, expectedDocumentIds: ['d1'] })
    wrapper.unmount()
  })

  it('leaves the count out when the run has none or its job is gone', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: true, questions: [], latestRuns: [run({ documentGone: null })] })
    vi.spyOn(api.evaluation, 'results').mockResolvedValue([])
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })
    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: false } })
    await flushPromises()
    expect(wrapper.find('[data-test=report]').exists()).toBe(true)
    expect(wrapper.find('[data-test=document-gone]').exists()).toBe(false)
    expect(wrapper.find('[data-test=deleted-notice]').exists()).toBe(false)
  })

  it('says the evaluation failed when its job can no longer be read, instead of running for ever', async () => {
    vi.useFakeTimers()
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: true, questions: [{ layer: 'human', state: 'active', count: 3 }], latestRuns: [] })
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.evaluation, 'run').mockResolvedValue({ jobId: 'j1', alreadyQueued: false })
    const get = vi.spyOn(api.jobs, 'get').mockRejectedValue(new ApiError(502, 'GET', null))
    const ui = useUiStore()

    const wrapper = mount(EvaluationTab, { props: { collectionId: 'c1', canEdit: true }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-test=run]').trigger('click')
    await flushPromises()

    expect(ui.toasts.map((t) => t.message)).toContain(EVALUATION.runFailed)
    expect(wrapper.get('[data-test=run]').text()).toBe(EVALUATION.run)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(get).toHaveBeenCalledTimes(1)
    wrapper.unmount()
    vi.useRealTimers()
  })
})
