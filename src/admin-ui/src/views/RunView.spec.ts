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

describe('Körning (admin design §5.5.3; #278 slice 2)', () => {
  it('shows the counters, the crawl report in Swedish and the failed documents with their reason', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue(run)
    const items = vi.spyOn(api.runs, 'items').mockResolvedValue({
      items: [{ id: 1, createdAt: '', documentId: null, identity: 'https://www.umea.se/saknas', outcome: 'failed', reason: 'http.404', strategy: null, durationMs: 12, costUsd: null }],
      next: null,
    })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()

    expect(wrapper.get('[data-test=state]').text()).toBe('Klar med anmärkning')
    expect(wrapper.get('[data-test=counts]').text()).toMatch(/9\s800/)
    const report = wrapper.get('[data-test=report]').text()
    expect(report).toContain('Webbplatsen gicks inte igenom helt:')
    expect(report).toContain('Webbplatsen har fler sidor än gränsen; bara de första hämtas.')
    expect(report).toContain('1 av 2 startadresser svarade')
    expect(wrapper.text()).toContain('Sidan finns inte (404). Kontrollera adressen.')

    await wrapper.get('input[type=checkbox]').setValue(true)
    await flushPromises()
    expect(items).toHaveBeenLastCalledWith('r1', expect.objectContaining({ outcome: 'failed' }))
  })

  it('shows the deletion decision when the guard held deletions back', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.runs, 'get').mockResolvedValue({ ...run, run: { ...run.run, state: 'awaiting-deletion-confirmation', proposedDeletions: 212 } })
    vi.spyOn(api.runs, 'items').mockResolvedValue({ items: [], next: null })
    const deletions = vi.spyOn(api.runs, 'deletions').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(RunView, { props: { id: 'r1' }, global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.get('[data-test=deletions]').text()).toContain('212 dokument hittades inte')
    expect(deletions).toHaveBeenCalledWith('r1', undefined)
  })
})
