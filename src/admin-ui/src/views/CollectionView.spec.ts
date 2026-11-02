import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Collection, DocumentSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { button } from '@/test/helpers'
import CollectionView from './CollectionView.vue'

const base: Collection = {
  id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'internal', publishedAt: null, ownerTeamId: null,
  embeddingProfileId: 'p', retrievalSettings: {}, usedBy: [{ id: 'a1', name: 'Bygglovsassistenten', classification: 'internal', published: false, draft: true }],
  updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '7',
}

function document(overrides: Partial<DocumentSummary>): DocumentSummary {
  return {
    id: 'doc', sourceId: 's1', title: 'Altan', url: 'https://www.umea.se/altan', displayName: null, mediaType: 'text/html', parseOutcome: 'ok',
    piiFlagged: false, piiCleared: false, held: false, live: true, sourceModifiedAt: null, fetchedAt: '2026-11-01T02:14:00Z', lastSeenAt: '2026-11-01T02:14:00Z',
    ...overrides,
  }
}

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

describe('Knowledge base (admin design §5.5.2)', () => {
  it('shows the state, who uses it, its sources, and documents filtered to personal data', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 0, lastRun: null },
    ])
    const list = vi.spyOn(api.documents, 'list').mockImplementation(async (q) => ({
      items: q.pii === 'flagged' ? [document({ id: 'p', title: 'Protokoll', piiFlagged: true })] : [document({}), document({ id: 'p', title: 'Protokoll', piiFlagged: true })],
      next: null,
    }))

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=state]').text()).toBe('Not published')
    expect(wrapper.text()).toContain('Not published: only visible in Test.')
    expect(wrapper.get('[data-test=used-by]').text()).toBe('Bygglovsassistenten (draft)')
    expect(wrapper.find('[data-test=publish]').exists()).toBe(false) // publishing is Manage

    await button(wrapper, 'Sources (1)').trigger('click')
    expect(wrapper.text()).toContain('umea.se/bygga')
    expect(wrapper.text()).toContain('Website')

    await button(wrapper, 'Documents').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('[data-test=pii]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Altan')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ collectionId: 'c1', pii: 'flagged' }))
    expect(wrapper.text()).not.toContain('Altan')
  })

  it('shows the document count, the last fetch with its result and the latest quality on the overview', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      {
        id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 2300,
        lastRun: { id: 'r1', state: 'succeeded', startedAt: '2026-10-30T01:00:00Z', finishedAt: '2026-10-30T01:12:00Z' },
      },
      {
        id: 's2', collectionId: 'c1', type: 'upload', name: 'Blanketter', classification: 'internal', state: 'active', documents: 24,
        lastRun: { id: 'r2', state: 'failed', startedAt: '2026-10-31T09:00:00Z', finishedAt: '2026-10-31T09:01:00Z' },
      },
    ])
    const run = (id: string, variant: string, recallAtK: number, candidate = false) => ({
      id, kind: 'nightly', variant, questionCount: 40, k: 10, found: 36, recallAtK, mrr: 0.8, wins: null, losses: null, comparedRunId: null, candidate,
      thresholdProposal: null, startedAt: '2026-11-01T02:00:00Z', finishedAt: '2026-11-01T02:05:00Z',
    })
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true, questions: [], latestRuns: [run('e3', 'hybrid', 0.95, true), run('e2', 'vector', 0.8), run('e1', 'hybrid', 0.91)],
    })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=document-count]').text().replace(/\s/g, '')).toBe('2324')
    expect(wrapper.get('[data-test=last-fetch]').text()).toContain('Failed')
    expect(wrapper.text()).toContain('Latest quality (recall@10)')
    expect(wrapper.get('[data-test=quality]').text()).toContain('0,91')
  })

  it('lets a department admin publish with the version it read', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const publish = vi.spyOn(api.collections, 'publish').mockResolvedValue({ ...base, publishedAt: '2026-11-01T11:00:00Z', version: '8' })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()

    expect(publish).toHaveBeenCalledWith('c1', '7')
    expect(wrapper.get('[data-test=state]').text()).toBe('Published')
  })

  it('takes the evaluation report threshold proposal to Search settings, filled in', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true,
      questions: [{ layer: 'human', state: 'active', count: 2 }],
      latestRuns: [{
        id: 'r1', kind: 'nightly', variant: 'hybrid', questionCount: 2, k: 10, found: 1, recallAtK: 0.5, mrr: 0.5, wins: null, losses: null, comparedRunId: null,
        candidate: false, thresholdProposal: { answerable: 1, unanswerable: 1, coverageMaxDistance: { value: 0.4, misclassified: 0 } },
        startedAt: '2026-11-01T02:00:00Z', finishedAt: '2026-11-01T02:01:00Z',
      }],
    })
    vi.spyOn(api.evaluation, 'results').mockResolvedValue([])
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await button(wrapper, 'Test questions and quality').trigger('click')
    await flushPromises()
    await wrapper.get('[data-test=use-proposal]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test=retrieval]').exists()).toBe(true)
    expect((wrapper.get('#rs-coverageMaxDistance').element as HTMLInputElement).value).toBe('0,4')
  })

  it('shows Get started on Overview until the checklist is done', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const setup = vi.spyOn(api.collections, 'setup').mockResolvedValue({
      collectionId: 'c1', name: 'Bygglov', sources: 0, firstSyncDone: false, syncRunning: false, testQuestions: 0, evaluated: false, evaluating: false,
      published: false, assistantId: null, assistantPublished: false, available: false, done: false,
    })
    const stubs = { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }
    const wrapper = mount(CollectionView, { props: { id: 'c1' }, ...stubs })
    await flushPromises()
    expect(wrapper.get('[data-test=setup]').text()).toContain('Add source')
    expect(wrapper.get('[data-test=step-source]').attributes('data-state')).toBe('todo')

    setup.mockResolvedValue({ ...(await setup.mock.results[0]!.value), done: true, available: true })
    const done = mount(CollectionView, { props: { id: 'c1' }, ...stubs })
    await flushPromises()
    expect(done.find('[data-test=setup]').exists()).toBe(false)
  })
})
