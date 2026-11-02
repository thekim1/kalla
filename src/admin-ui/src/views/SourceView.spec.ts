import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { RunSummary, Source } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import SourceView from './SourceView.vue'

const push = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

const source: Source = {
  id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'public', state: 'active',
  settings: { startUrls: ['https://www.umea.se/bygga'], maxPages: 2500 }, schedule: null, pipelineId: 'p', containsThirdPartyText: true,
  updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '3',
}

function run(overrides: Partial<RunSummary>): RunSummary {
  return {
    id: 'r1', kind: 'sync', trigger: 'manual', state: 'succeeded', phase: null, added: 40, updated: 2, unchanged: 0, deleted: 1, failed: 3, proposedDeletions: 0,
    costUsd: 0, startedAt: '2026-11-01T01:12:00Z', finishedAt: '2026-11-01T01:20:00Z', ...overrides,
  }
}

describe('Källa (admin design §5.5.3; #278 slice 2)', () => {
  it('shows the settings, runs with their counts, the personal-data report, and starts a sync', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    const runs = vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run({})], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue({
      applies: true, piiMode: 'hold', collectionClassification: 'public', truncated: false,
      documents: [{ documentId: 'd', title: 'Protokoll', url: null, displayName: null, state: 'held', previousVersionLive: false, counts: {}, rulesFired: 1 }],
    })
    const sync = vi.spyOn(api.sources, 'sync').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'sync' })

    const wrapper = mount(SourceView, { props: { id: 's1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=addresses]').text()).toBe('https://www.umea.se/bygga')
    expect(wrapper.text()).toMatch(/2\s500/)
    expect(wrapper.text()).toContain('Klar')
    expect(wrapper.text()).toContain('+40 · ~2 · −1')
    expect(wrapper.get('[data-test=pii]').text()).toContain('Hålls utanför sökningen')

    await wrapper.get('[data-test=sync]').trigger('click')
    await flushPromises()
    expect(sync).toHaveBeenCalledWith('s1')
    expect(runs).toHaveBeenCalledTimes(2) // the list reloads with the new run

    await wrapper.findAll('tbody tr')[0]!.find('button').trigger('click')
    expect(push).toHaveBeenCalledWith({ name: 'run', params: { id: 'r1' } })
  })

  it('shows the Nedprioritera rules and opens Ändra källa with the collection for its level ceiling', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue({ ...source, settings: { ...source.settings, demote: [{ pattern: 'www.umea.se/bygga/**/arkiv/**', factor: 0.5 }] } })
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.sources, 'piiReport').mockRejectedValue(new Error('x'))
    const collection = vi.spyOn(api.collections, 'get').mockResolvedValue({ id: 'c1', classification: 'internal' } as never)

    const wrapper = mount(SourceView, { props: { id: 's1' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.get('[data-test=demoted]').text()).toBe('www.umea.se/bygga/**/arkiv/** ×0,5')
    await wrapper.get('[data-test=edit-source]').trigger('click')
    await flushPromises()
    expect(collection).toHaveBeenCalledWith('c1')
    expect(document.body.querySelector('#edit-name')).not.toBeNull()
    expect([...document.body.querySelectorAll<HTMLOptionElement>('#edit-level option')].map((o) => o.value)).toEqual(['public', 'internal'])
    wrapper.unmount()
  })
})
