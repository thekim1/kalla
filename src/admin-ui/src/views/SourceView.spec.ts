import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
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
  settings: { startUrls: ['https://www.umea.se/bygga'], maxPages: 2500 }, schedule: null, scheduleInForce: '0 1 * * 6',
  nextRunAt: '2099-11-28T00:44:00Z', pipelineId: 'p', containsThirdPartyText: true,
  updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '3',
}

function run(overrides: Partial<RunSummary>): RunSummary {
  return {
    id: 'r1', kind: 'sync', trigger: 'manual', state: 'succeeded', phase: null, added: 40, updated: 2, unchanged: 0, deleted: 1, failed: 3, proposedDeletions: 0,
    costUsd: 0, startedAt: '2026-11-01T01:12:00Z', finishedAt: '2026-11-01T01:20:00Z', ...overrides,
  }
}

describe('Source (admin design §5.5.3; #278 slice 2)', () => {
  it('shows the settings, runs with their counts, the personal-data report, and starts a sync', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    const runs = vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run({})], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue({
      applies: true, piiMode: 'hold', collectionClassification: 'public', truncated: false,
      documents: [
        { documentId: 'e', title: 'Kontakt', url: null, displayName: null, state: 'live', previousVersionLive: false, counts: { phone: 4 }, rulesFired: 0, typesFired: [], piiClearedAt: null },
        {
          documentId: 'd', title: 'Protokoll', url: null, displayName: null, state: 'held', previousVersionLive: false,
          counts: { personnummer: 1, email: 3, emailExternal: 2 }, rulesFired: 2, typesFired: ['emailExternal', 'personnummer'], piiClearedAt: null,
        },
      ],
    })
    const sync = vi.spyOn(api.sources, 'sync').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'sync' })

    const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    expect(wrapper.get('[data-test=addresses]').text()).toBe('https://www.umea.se/bygga')
    // Engine design §9.6: the schedule in force, said to be the default, and when the next scheduled fetch starts.
    expect(wrapper.get('[data-test=schedule]').text()).toBe('Every Saturday night at 01:00 (the platform default)')
    expect(wrapper.get('[data-test=next-run]').text()).toMatch(/2099/)
    expect(wrapper.text()).toMatch(/2\s500/)
    expect(wrapper.text()).toContain('Done')
    expect(wrapper.text()).toContain('40 added, 2 changed, 1 removed')
    // "3 failed" links to the run's page (admin design §5.5.3).
    expect(wrapper.get('[data-test=run-failed]').text()).toBe('3 failed')
    expect(wrapper.getComponent<typeof RouterLinkStub>('[data-test=run-failed]').props('to')).toEqual({ name: 'run', params: { id: 'r1' } })
    // The personal-data report is a table folded by default, its summary counting the documents and the flagged ones,
    // flagged documents first.
    const report = wrapper.get('[data-test=pii]')
    expect(report.element.tagName).toBe('DETAILS')
    expect((report.element as HTMLDetailsElement).open).toBe(false)
    expect(wrapper.get('[data-test=pii-summary]').text()).toBe('Personal data (2 documents, 1 flagged)')
    expect(report.findAll('[data-test=pii-document]').map((d) => d.text())).toEqual(['Protokoll', 'Kontakt'])
    expect(report.text()).toContain('Kept out of the search')
    // #217: the stored fired types and the counts, as type names and numbers (never values).
    const fired = wrapper.findAll('[data-test=pii-fired]')
    expect(fired.map((f) => f.text())).toEqual(['External email addresses, Personal identity numbers'])
    expect(wrapper.findAll('[data-test=pii-counts]').map((c) => c.text())).toEqual([
      'Personal identity numbers: 1, Email addresses: 3, External email addresses: 2',
      'Phone numbers: 4',
    ])
    // B-63: an editor (not Manage) gets no Show highlights or Clear.
    expect(wrapper.find('[data-test=pii-clear]').exists()).toBe(false)

    await wrapper.get('[data-test=sync]').trigger('click')
    await flushPromises()
    expect(sync).toHaveBeenCalledWith('s1')
    expect(runs).toHaveBeenCalledTimes(2) // the list reloads with the new run

    // A run row is a link to the run (§5.5.2 "Lists open as links"); a click elsewhere on the row opens it too.
    const row = wrapper.findAll('tbody tr')[0]!
    expect(row.getComponent(RouterLinkStub).props('to')).toEqual({ name: 'run', params: { id: 'r1' } })
    await row.findAll('td')[0]!.trigger('click')
    expect(push).toHaveBeenCalledWith({ name: 'run', params: { id: 'r1' } })
  })

  it('offers Manage the highlights and Clear on a flagged document, and shows a cleared one as cleared (B-63)', async () => {
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-department-admin'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue({
      applies: true, piiMode: 'report', collectionClassification: 'public', truncated: false,
      documents: [
        { documentId: 'd', title: 'Protokoll', url: null, displayName: null, state: 'held', previousVersionLive: false, counts: { personnummer: 1 }, rulesFired: 1,
          typesFired: ['personnummer'], piiClearedAt: null },
        { documentId: 'c', title: 'Kontakt', url: null, displayName: null, state: 'live', previousVersionLive: false, counts: { emailExternal: 1 }, rulesFired: 1,
          typesFired: ['emailExternal'], piiClearedAt: '2026-11-23T09:00:00Z' },
        // Cleared once, then a later version flagged and held again (processing §9.5): it can be cleared again (#963 review).
        { documentId: 'r', title: 'Nyhet', url: null, displayName: null, state: 'held', previousVersionLive: true, counts: { personnummer: 2 }, rulesFired: 1,
          typesFired: ['personnummer'], piiClearedAt: '2026-11-20T09:00:00Z' },
      ],
    })
    const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(wrapper.findAll('[data-test=pii-clear]')).toHaveLength(2) // the held two; the cleared live one has none
    expect(wrapper.findAll('[data-test=pii-cleared]')).toHaveLength(1) // not on the one held again
    wrapper.unmount()
  })

  it('says a source’s own schedule as it is, and that a paused source has no next scheduled fetch (engine design §9.6)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue({ ...source, schedule: '0 6 * * 1-5', scheduleInForce: '0 6 * * 1-5', state: 'paused', nextRunAt: null })
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue(null as never)

    const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(wrapper.get('[data-test=schedule]').text()).toBe('0 6 * * 1-5')
    expect(wrapper.get('[data-test=next-run]').text()).toBe('None: the source is paused')
  })

  it('offers Continue beside Sync now when the latest crawl run can be continued, and reloads the runs (#804)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    const runs = vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run({ state: 'failed' })], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue({ applies: false, piiMode: 'off', collectionClassification: 'public', truncated: false, documents: [] })
    vi.spyOn(api.runs, 'get').mockResolvedValue({
      run: run({ state: 'failed' }), sourceId: 's1', pipelineVersionId: null, scheduledFor: null, report: null, continuesRunId: null, canContinue: true,
    })
    const started = vi.spyOn(api.runs, 'continue').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'continue' })

    const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    runs.mockClear()
    await wrapper.get('[data-test=continue-run]').trigger('click')
    await flushPromises()

    expect(started).toHaveBeenCalledWith('r1')
    expect(runs).toHaveBeenCalled()
  })

  it('offers no Continue for a source that is not a crawl', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue({ ...source, type: 'url-list', settings: { urls: ['https://www.umea.se/a'] } })
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [run({ state: 'failed' })], next: null })
    vi.spyOn(api.sources, 'piiReport').mockResolvedValue({ applies: false, piiMode: 'off', collectionClassification: 'public', truncated: false, documents: [] })
    const get = vi.spyOn(api.runs, 'get')

    const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()

    expect(get).not.toHaveBeenCalled()
    expect(wrapper.find('[data-test=continue-run]').exists()).toBe(false)
  })

  it('shows the Demote rules and opens Edit source with the collection for its level ceiling', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue({
      ...source, settings: { ...source.settings, sitemaps: ['https://www.umea.se/sitemap1.xml.gz'], demote: [{ pattern: 'www.umea.se/bygga/**/arkiv/**', factor: 0.5 }] },
    })
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.sources, 'piiReport').mockRejectedValue(new Error('x'))
    const collection = vi.spyOn(api.collections, 'get').mockResolvedValue({ id: 'c1', classification: 'internal' } as never)

    const wrapper = mount(SourceView, { props: { id: 's1' }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(wrapper.get('[data-test=demoted]').text()).toBe('www.umea.se/bygga/**/arkiv/** ×0,5')
    expect(wrapper.get('[data-test=sitemaps]').text()).toBe('https://www.umea.se/sitemap1.xml.gz')
    await wrapper.get('[data-test=edit-source]').trigger('click')
    await flushPromises()
    expect(collection).toHaveBeenCalledWith('c1')
    expect(document.body.querySelector('#edit-name')).not.toBeNull()
    expect([...document.body.querySelectorAll<HTMLOptionElement>('#edit-level option')].map((o) => o.value)).toEqual(['public', 'internal'])
    wrapper.unmount()
  })

  it('deletes the source for Manage after its name is typed; an editor has no Delete (Decisions "Deleting knowledge bases, sources and assistants")', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-department-admin'] })
    auth.status = 'authenticated'
    vi.spyOn(api.sources, 'get').mockResolvedValue(source)
    vi.spyOn(api.sources, 'runs').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.sources, 'piiReport').mockRejectedValue(new Error('x'))
    vi.spyOn(api.collections, 'sources').mockResolvedValue([{ id: 's1', documents: 1234 } as never])
    const remove = vi.spyOn(api.sources, 'remove').mockResolvedValue(undefined)
    push.mockClear()

    const wrapper = mount(SourceView, { props: { id: 's1' }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.get('[data-test=delete]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toMatch(/Its 1\s234 documents leave the search at once/)
    const confirm = document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!
    // The button waits for the name.
    expect(confirm.getAttribute('aria-disabled')).toBe('true')
    confirm.click()
    await flushPromises()
    expect(remove).not.toHaveBeenCalled()
    const name = document.body.querySelector<HTMLInputElement>('[data-test=confirm-name] input, input[data-test=confirm-name]')!
    name.value = 'umea.se/bygga'
    name.dispatchEvent(new Event('input'))
    await flushPromises()
    expect(confirm.getAttribute('aria-disabled')).not.toBe('true')
    confirm.click()
    await flushPromises()
    expect(remove).toHaveBeenCalledWith('s1', '3')
    expect(push).toHaveBeenCalledWith({ name: 'collection', params: { id: 'c1' } })
    wrapper.unmount()

    auth.user = bffUser({ roles: ['rag-editor'] })
    const editor = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(editor.find('[data-test=delete]').exists()).toBe(false)
  })

  it('polls a running run, shows its progress in place and reloads the runs when it ends (#803)', async () => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
    try {
      const auth = useAuthStore()
      auth.user = bffUser({ roles: ['rag-editor'] })
      auth.status = 'authenticated'
      vi.spyOn(api.sources, 'get').mockResolvedValue(source)
      vi.spyOn(api.sources, 'piiReport').mockRejectedValue(new Error('x'))
      const progress = { processed: 10, admitted: 40, maxPages: 2500, waiting: 30, seeds: 1, seedsFailed: 0, limitHit: false }
      const running = run({ id: 'r2', state: 'running', phase: 'fetch', finishedAt: null, progress })
      const runs = vi.spyOn(api.sources, 'runs')
        .mockResolvedValueOnce({ items: [running, run({})], next: null })
        .mockResolvedValue({ items: [run({ id: 'r2' }), run({})], next: null })
      const get = vi.spyOn(api.runs, 'get')
        .mockResolvedValueOnce({ run: { ...running, progress: { ...progress, processed: 25, waiting: 15 } } } as never)
        .mockResolvedValueOnce({ run: run({ id: 'r2' }) } as never)

      const wrapper = mount(SourceView, { props: { id: 's1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
      await flushPromises()
      expect(wrapper.get('[data-test=run-progress]').text()).toBe('Fetching and processing pages · 10 handled · 30 waiting · 40 found (limit 2\u00a0500)')

      await vi.advanceTimersByTimeAsync(2_000)
      await flushPromises()
      expect(get).toHaveBeenCalledWith('r2')
      expect(wrapper.get('[data-test=run-progress]').text()).toContain('25 handled · 15 waiting')
      expect(runs).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(2_000)
      await flushPromises()
      expect(runs).toHaveBeenCalledTimes(2)
      expect(wrapper.find('[data-test=run-progress]').exists()).toBe(false)
      await vi.advanceTimersByTimeAsync(20_000)
      // Two polls, then one read of the ended run for Continue (#804); no further polls.
      expect(get).toHaveBeenCalledTimes(3)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })
})
