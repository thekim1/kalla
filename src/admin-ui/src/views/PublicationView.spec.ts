import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { EvaluationRun, Pipeline, Publication, RunSummary, Source } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import PublicationView from './PublicationView.vue'

const definition = { schema: 1, template: 'website', templateVersion: 1, params: {} }
const publication: Publication = {
  id: 'pub1', state: 'succeeded', lastError: null, pipelineId: 'p1', pipelineVersionId: 'v3', draftNumber: 3, draftState: 'draft', version: '17',
  published: false, superseded: false,
  plan: { pipelineVersionId: 'v3', documents: 1240, sources: [{ sourceId: 's1', documents: 1240 }], estimatedMinutes: 85, estimatedCostUsd: 0 },
}
const pipeline = (state: string, publishedAt: string | null): Pipeline => ({
  id: 'p1', name: 'website-bygglov', templateId: 'website', departmentId: 'd1', publishedVersionId: state === 'published' ? 'v3' : 'v2', version: '18',
  versions: [
    { id: 'v3', number: 3, state, definition, publishedAt },
    { id: 'v2', number: 2, state: state === 'draft' ? 'published' : 'superseded', definition, publishedAt: '2026-10-30T10:00:00Z' },
    { id: 'v1', number: 1, state: 'superseded', definition, publishedAt: '2026-10-01T10:00:00Z' },
  ],
})
const source = { id: 's1', name: 'umea.se/bygga', collectionId: 'c1' } as Source
const run = (state: string): RunSummary => ({
  id: 'r1', kind: 'sync', trigger: 'publish', state, phase: null, added: 0, updated: 0, unchanged: 0, deleted: 0, failed: 0, proposedDeletions: 0, costUsd: 0,
  startedAt: '2026-11-02T08:01:00Z', finishedAt: state === 'running' ? null : '2026-11-02T08:30:00Z',
})
const evalRun = (id: string, recall: number, compared: string | null, startedAt: string): EvaluationRun => ({
  id, kind: 'publish-report', variant: 'hybrid', questionCount: 86, k: 5, found: 67, recallAtK: recall, mrr: 0.6, wins: 4, losses: 1, comparedRunId: compared,
  candidate: false, thresholdProposal: null, startedAt, finishedAt: startedAt,
})

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

const confirmButton = (label: string) => [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === label).at(-1)!

afterEach(() => vi.restoreAllMocks())

describe('Publicering (admin design §6.6, §6.7)', () => {
  it('shows the plan and confirms it with the pipeline version; then follows the re-processing', async () => {
    signIn(['rag-department-admin'])
    const get = vi.spyOn(api.publications, 'get').mockResolvedValue(publication)
    vi.spyOn(api.pipelines, 'get').mockResolvedValue(pipeline('draft', null))
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    const runs = vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run('running')], next: null })
    const confirm = vi.spyOn(api.publications, 'confirm').mockImplementation(async () => {
      get.mockResolvedValue({ ...publication, published: true })
      vi.spyOn(api.pipelines, 'get').mockResolvedValue(pipeline('published', '2026-11-02T08:00:00Z'))
      return { ...publication, published: true }
    })
    const wrapper = mount(PublicationView, { props: { id: 'pub1' }, attachTo: document.body })
    await flushPromises()

    const plan = wrapper.get('[data-test=plan]').text().replace(/\u00a0/g, ' ')
    expect(plan).toContain('1 240 dokument i 1 källa bearbetas om.')
    expect(plan).toContain('cirka 1,4 h')
    expect(wrapper.get('[data-test=affected]').text()).toContain('umea.se/bygga')
    expect(runs).not.toHaveBeenCalled()

    await wrapper.get('[data-test=confirm]').trigger('click')
    await flushPromises()
    confirmButton('Bekräfta').click()
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith('pub1', '17')
    expect(wrapper.get('[data-test=state]').text()).toBe('Publicerad')
    expect(wrapper.get('[data-test=affected]').text()).toContain('Pågår')
    expect(wrapper.get('[data-test=report]').text()).toContain('Källorna bearbetas om sedan')
    wrapper.unmount()
  })

  it('shows the evaluation report after the re-processing and rolls back to the version published before', async () => {
    signIn(['rag-pipeline-editor'])
    vi.spyOn(api.publications, 'get').mockResolvedValue({ ...publication, published: true, draftState: 'published' })
    vi.spyOn(api.pipelines, 'get').mockResolvedValue(pipeline('published', '2026-11-02T08:00:00Z'))
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run('succeeded')], next: null })
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true, questions: [],
      latestRuns: [evalRun('e2', 0.78, 'e1', '2026-11-02T08:31:00Z'), evalRun('e1', 0.72, null, '2026-10-30T10:30:00Z')],
    })
    const rollback = vi.spyOn(api.pipelines, 'rollback').mockResolvedValue(pipeline('superseded', '2026-11-02T08:00:00Z'))
    const wrapper = mount(PublicationView, { props: { id: 'pub1' }, attachTo: document.body })
    await flushPromises()

    const report = wrapper.get('[data-test=report]').text()
    expect(report).toContain('Träffsäkerhet 0,78 · 86 frågor')
    expect(report).toContain('Före: 0,72 · 4 bättre, 1 sämre')
    expect(wrapper.find('[data-test=confirm]').exists()).toBe(false)

    await wrapper.get('[data-test=rollback]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Version 2 publiceras igen och källorna bearbetas om.')
    confirmButton('Återställ').click()
    await flushPromises()
    expect(rollback).toHaveBeenCalledWith('p1', { versionId: 'v2', version: '18' })
    wrapper.unmount()
  })

  it('says when a newer plan has replaced this one, and keeps a reader from confirming', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.publications, 'get').mockResolvedValue({ ...publication, superseded: true })
    vi.spyOn(api.pipelines, 'get').mockResolvedValue(pipeline('draft', null))
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    const wrapper = mount(PublicationView, { props: { id: 'pub1' } })
    await flushPromises()
    expect(wrapper.text()).toContain('En nyare plan finns för samma bearbetning.')
    expect(wrapper.find('[data-test=confirm]').exists()).toBe(false)
    wrapper.unmount()
  })
})
