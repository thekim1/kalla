import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Run } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { RUN_PAGE } from '@/utils/labels'
import RunView from './RunView.vue'

const push = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

const run: Run = {
  run: {
    id: 'r1', kind: 'sync', trigger: 'manual', state: 'degraded', phase: null, added: 9800, updated: 0, unchanged: 0, deleted: 0, failed: 2, proposedDeletions: 0,
    costUsd: 0, startedAt: '2026-11-01T01:12:00Z', finishedAt: '2026-11-01T03:00:00Z',
  },
  sourceId: 's1', pipelineVersionId: null, scheduledFor: null, continuesRunId: null, canContinue: false,
  report: {
    completeness: { complete: false, reasons: ['crawl.limit_pages'], seeds: 2, seedsResolved: 1, seedsFailed: 1, incremental: false },
    verdict: { runFailure: null, runDegradation: 'crawl.limit_pages' },
  },
}

describe('Run (admin design §5.5.3; #278 slice 2)', () => {
  it('offers Continue on a run that can be continued and opens the source when it started (#804)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.runs, 'get').mockResolvedValue({ ...run, canContinue: true })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })
    const started = vi.spyOn(api.runs, 'continue').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'continue' })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    await wrapper.get('[data-test=continue-run]').trigger('click')
    await flushPromises()

    expect(started).toHaveBeenCalledWith('r1')
    expect(push).toHaveBeenCalledWith({ name: 'source', params: { id: 's1' } })
  })

  it('says what a continue run took over, what the scope dropped and the page limit, and offers no Continue when it cannot (#804)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.runs, 'get').mockResolvedValue({
      ...run, continuesRunId: 'r0',
      report: { ...run.report, continued: { from: 'r0', open: 120, failed: 3, dropped: 2, admitted: 100 } },
    })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    const card = wrapper.get('[data-test=continued]').text()
    expect(card).toContain('Continued from')
    expect(card).toContain('Took over 120 pages left open and 3 failed pages.')
    expect(card).toContain('2 pages were left out because they are no longer within the scope or the depth limit.')
    expect(card).toContain('Only 100 of them were fetched before the page limit.')
    expect(wrapper.find('[data-test=continue-run]').exists()).toBe(false)
  })

  it('shows the counters, the crawl report in Swedish and the failed documents with their reason', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue(run)
    const items = vi.spyOn(api.runs, 'items').mockResolvedValue({
      items: [{ id: 1, createdAt: '', documentId: null, identity: 'https://www.umea.se/saknas', outcome: 'failed', reason: 'http.404', strategy: null, durationMs: 12, costUsd: null }],
      next: null,
    })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    expect(wrapper.get('[data-test=state]').text()).toBe('Done with remarks')
    expect(wrapper.get('[data-test=counts]').text()).toMatch(/9\s800/)
    const report = wrapper.get('[data-test=report]').text()
    expect(report).toContain('The website was not gone through completely:')
    expect(report).toContain('The website has more pages than the limit; only the first ones are fetched.')
    expect(report).toContain('1 of 2 start addresses answered')
    expect(wrapper.text()).toContain('The page does not exist (404). Check the address.')

    await wrapper.get('input[type=checkbox]').setValue(true)
    await flushPromises()
    expect(items).toHaveBeenLastCalledWith('r1', expect.objectContaining({ outcome: 'failed' }))
  })

  it('shows a running crawl’s progress, refreshes it and the documents with each poll, and stops when the run ends (#803)', async () => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
    try {
      const running: Run = {
        ...run,
        report: null,
        run: {
          ...run.run, state: 'running', phase: 'fetch', added: 3, failed: 1, finishedAt: null,
          progress: { processed: 4, admitted: 1200, maxPages: 5000, waiting: 96, seeds: 2, seedsFailed: 1, limitHit: false },
        },
      }
      const done: Run = { ...run, run: { ...run.run, progress: null } }
      const get = vi.spyOn(api.runs, 'get').mockResolvedValueOnce(running).mockResolvedValueOnce(done)
      const items = vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

      const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
      await flushPromises()

      const progress = wrapper.get('[data-test=progress]').text()
      expect(progress).toMatch(/Fetching and processing pages · 4 handled · 96 waiting · 1\s200 found \(limit 5\s000\)/)
      expect(progress).toContain('1 of 2 start addresses did not answer')
      expect(items).toHaveBeenCalledTimes(1)

      await vi.advanceTimersByTimeAsync(2_000)
      await flushPromises()
      expect(get).toHaveBeenCalledTimes(2)
      expect(wrapper.find('[data-test=progress]').exists()).toBe(false)
      expect(items).toHaveBeenCalledTimes(2)
      await vi.advanceTimersByTimeAsync(20_000)
      expect(get).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })

  it('says a run’s files were not scanned for malware when its report says malwareScan off, and nothing otherwise (#535)', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.runs, 'get').mockResolvedValueOnce({ ...run, report: { ...run.report, malwareScan: 'off' } }).mockResolvedValueOnce(run)
    const off = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(off.get('[data-test=not-scanned]').text()).toBe(RUN_PAGE.notScanned)
    const on = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(on.find('[data-test=not-scanned]').exists()).toBe(false)
  })

  it('names the websites fetched with their own politeness and why robots.txt was ignored (connectors design §6.6)', async () => {
    setActivePinia(createPinia())
    const none = {
      requestsPerSecond: null, maxConnections: null, crawlDelay: null, adaptToLatency: null, crawlWindow: null, maxRetryAfterSeconds: null,
      maxBackoffFactor: null, breakerOff: null, breakerThreshold: null, breakerPauseMinutes: null, robotsIgnoredReason: null, userAgent: null, renderPerHost: null,
    }
    vi.spyOn(api.runs, 'get').mockResolvedValue({
      ...run,
      report: {
        ...run.report,
        politeness: [{ ...none, host: 'www.umea.se', preset: 'fast', requestsPerSecond: 20, crawlWindow: 'any', breakerOff: true, robotsIgnoredReason: 'own site, web team consent' }],
      },
    })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    expect(wrapper.get('[data-test=politeness]').text()).toContain(
      'www.umea.se: Fast (10 a second, any time) · 20 requests a second · any time · circuit breaker off · robots.txt ignored: own site, web team consent',
    )
  })

  it('counts the rendered pages, those with no main text, render failures and pages kept from their last good content of a browser crawl (connectors design §8.6)', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue({ ...run, report: { ...run.report, render: { rendered: 1240, empty: 4, failures: 3, keptFromLastGood: 2 } } })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    const card = wrapper.get('[data-test=render]').text().replace(/\s+/g, ' ')
    expect(card).toContain('Pages fetched with a browser')
    expect(card).toContain('Rendered1 240')
    expect(card).toContain('Rendered with no main text4')
    expect(card).toContain('Render failures3')
    expect(card).toContain('Kept from their last good content2')
  })

  it('has no renderer card for an HTTP crawl', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue(run)
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })
    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.find('[data-test=render]').exists()).toBe(false)
  })

  it('shows the deletion decision when the guard held deletions back', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue({ ...run, run: { ...run.run, state: 'awaiting-deletion-confirmation', proposedDeletions: 212 } })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })
    const deletions = vi.spyOn(api.runs, 'deletions').mockResolvedValue({ items: [], next: null, guard: { percent: 20, minCount: 5, maxCount: 500 } })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.get('[data-test=deletions]').text()).toContain('212 documents were not found')
    expect(deletions).toHaveBeenCalledWith('r1', undefined)
  })

  it('stops polling a running crawl when a refresh fails, and shows the error with Retry', async () => {
    setActivePinia(createPinia())
    vi.useFakeTimers()
    try {
      const running: Run = { ...run, report: null, run: { ...run.run, state: 'running', finishedAt: null } }
      const get = vi.spyOn(api.runs, 'get').mockResolvedValueOnce(running).mockRejectedValueOnce(new Error('offline'))
      vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

      const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
      await flushPromises()
      await vi.advanceTimersByTimeAsync(2_000)
      await flushPromises()
      expect(get).toHaveBeenCalledTimes(2)
      expect(wrapper.find('[role=alert]').exists()).toBe(true)
      await vi.advanceTimersByTimeAsync(60_000)
      expect(get).toHaveBeenCalledTimes(2)
    } finally {
      vi.useRealTimers()
    }
  })
})
