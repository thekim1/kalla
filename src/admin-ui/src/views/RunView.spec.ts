import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Run } from '@/api/types'
import RunView from './RunView.vue'

const run: Run = {
  run: {
    id: 'r1', kind: 'sync', trigger: 'manual', state: 'degraded', phase: null, added: 9800, updated: 0, unchanged: 0, deleted: 0, failed: 2, proposedDeletions: 0,
    costUsd: 0, startedAt: '2026-11-01T01:12:00Z', finishedAt: '2026-11-01T03:00:00Z',
  },
  sourceId: 's1', pipelineVersionId: null, scheduledFor: null,
  report: {
    completeness: { complete: false, reasons: ['crawl.limit_pages'], seeds: 2, seedsResolved: 1, seedsFailed: 1, incremental: false },
    verdict: { runFailure: null, runDegradation: 'crawl.limit_pages' },
  },
}

describe('Run (admin design §5.5.3; #278 slice 2)', () => {
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

  it('counts the rendered pages, render failures and pages kept from their last good content of a browser crawl (connectors design §8.6)', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue({ ...run, report: { ...run.report, render: { rendered: 1240, failures: 3, keptFromLastGood: 2 } } })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    const card = wrapper.get('[data-test=render]').text().replace(/\s+/g, ' ')
    expect(card).toContain('Pages fetched with a browser')
    expect(card).toContain('Rendered1 240')
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
    const deletions = vi.spyOn(api.runs, 'deletions').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.get('[data-test=deletions]').text()).toContain('212 documents were not found')
    expect(deletions).toHaveBeenCalledWith('r1', undefined)
  })
})
