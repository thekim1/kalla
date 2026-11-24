import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { FeedbackItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { FEEDBACK, FILTERS } from '@/utils/labels'
import FeedbackView from './FeedbackView.vue'

// `?documentId=` (Show the ratings on Feedback on documents, §8.8) through a route the tests set.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const replace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRoute: () => route, useRouter: () => ({ replace }) }))

function signIn(roles: string[], openWebUi = true) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles, openWebUi: { enabled: openWebUi } })
  auth.status = 'authenticated'
}

function item(overrides: Partial<FeedbackItem> = {}): FeedbackItem {
  return {
    id: 'f1', createdAt: '2026-11-02T09:00:00Z', rating: -1, score: null, reasonCategory: 'not_factually_correct', reviewState: 'open', hasComment: false,
    source: 'owui-import', assistantId: 'a1', assistantName: 'Bygglovsassistenten', assistantVersion: 3, clientId: 'c1', clientName: 'Open WebUI (pilot)',
    outcome: 'answered', coverage: 'ok', language: 'sv', citedDocuments: [{ id: 'd1', title: 'Altan och uteplats' }], ...overrides,
  }
}

afterEach(() => {
  vi.restoreAllMocks()
  route.query = {}
})

describe('Feedback (admin design §8.1)', () => {
  it('shows ratings without content, the totals and import health, filters, and marks an item handled', async () => {
    signIn(['rag-department-admin'])
    const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [item(), item({ id: 'f2', rating: 1, reasonCategory: null, outcome: 'not-found', citedDocuments: [] })], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({
      lastUsedOn: null,
      totals: { answers: 200, notFound: 12, thumbsUp: 41, thumbsDown: 9 },
      imports: [
        { clientId: 'c1', clientName: 'Open WebUI (pilot)', lastRunAt: '2026-11-02T09:15:00Z', lastOutcome: 'ok', unmapped: 2, failedRuns: 0, alert: false },
        { clientId: 'c2', clientName: 'Open WebUI (test)', lastRunAt: '2026-11-02T09:15:00Z', lastOutcome: 'failed:401', unmapped: 0, failedRuns: 4, alert: true },
      ],
    })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const mark = vi.spyOn(api.feedback, 'setReviewState').mockResolvedValue({ id: 'f1', reviewState: 'reviewed' })

    const wrapper = mount(FeedbackView)
    await flushPromises()

    const totals = wrapper.get('[data-test=totals]').text()
    expect(totals).toContain('200')
    expect(totals).toMatch(/12 not found \(6\s*%\)/)
    expect(totals).toContain('41 up, 9 down')
    expect(wrapper.get('[data-test=imports]').text()).toContain('2 could not be matched')
    // chat §14.3: the alert after 4 failed runs in a row, on that client only (B-23).
    const alerts = wrapper.findAll('[data-test=import-alert]')
    expect(alerts).toHaveLength(1)
    expect(alerts[0]!.text()).toContain('The last 4 imports failed')
    expect(wrapper.text()).toContain('Not factually correct')
    expect(wrapper.text()).toContain('version 3')
    expect(wrapper.text()).toContain('Altan och uteplats')
    expect(wrapper.text()).toContain('Found nothing')
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ state: 'open' }))

    await wrapper.get('[data-test="chip-Rating"] select').setValue('down')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ rating: '-1' }))

    await wrapper.findAll('[data-test=mark]')[0]!.trigger('click')
    await flushPromises()
    expect(mark).toHaveBeenCalledWith('f1', 'reviewed')
    expect(wrapper.text()).toContain('Handled')
  })

  it('shows no mark buttons to a viewer', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [item()], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ lastUsedOn: null, totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()
    expect(wrapper.find('[data-test=mark]').exists()).toBe(false)
    // No answers: the count, not a dash that reads as "— found nothing".
    expect(wrapper.get('[data-test=totals]').text()).toContain('0 not found')
    expect(wrapper.get('[data-test=totals]').text()).not.toContain('—')
  })

  it('says that feedback comes from Open WebUI while the deployment runs it', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ lastUsedOn: null, totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()

    expect(wrapper.text()).toContain(FEEDBACK.empty)
    expect(wrapper.get('[data-test=imports]').text()).toContain('Import from Open WebUI')
  })

  it('without Open WebUI shows no import block and names the API, and still lists the stored feedback', async () => {
    signIn(['rag-viewer'], false)
    const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [], next: null })
    // An import that ran before the profile was turned off: its health is not shown, as nothing is imported any more.
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({
      lastUsedOn: null,
      totals: { answers: 3, notFound: 0, thumbsUp: 1, thumbsDown: 0 },
      imports: [{ clientId: 'c2', clientName: 'Open WebUI (test)', lastRunAt: '2026-11-02T09:15:00Z', lastOutcome: 'failed:401', unmapped: 0, failedRuns: 4, alert: true }],
    })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()

    expect(wrapper.find('[data-test=imports]').exists()).toBe(false)
    expect(wrapper.find('[data-test=import-alert]').exists()).toBe(false)
    expect(wrapper.get('[data-test=totals]').text()).toContain('1 up, 0 down')
    expect(wrapper.text()).toContain(FEEDBACK.emptyApi)
    expect(wrapper.text()).not.toContain('Open WebUI')
    // The endpoint is behind Details, not in the sentence (admin design §5.7).
    expect(FEEDBACK.emptyApi).not.toContain('/v1/feedback')
    expect(wrapper.get('[data-test=feedback-api-details] summary').text()).toBe('Details')
    expect(wrapper.get('[data-test=feedback-api-details]').text()).toContain('POST /v1/feedback')

    list.mockResolvedValue({ items: [item()], next: null })
    await wrapper.get('[data-test="chip-Rating"] select').setValue('down')
    await flushPromises()
    expect(wrapper.text()).toContain('Altan och uteplats')
  })

  it('filters by the cited document from ?documentId=, names it from a row, and shows all documents again', async () => {
    signIn(['rag-department-admin'])
    route.query = { documentId: 'd1', tab: 'x' }
    const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({
      items: [item({ citedDocuments: [{ id: 'd0', title: 'Bygglov' }, { id: 'd1', title: 'Altan och uteplats' }] })], next: null,
    })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ lastUsedOn: null, totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()

    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ documentId: 'd1' }))
    expect(wrapper.get('[data-test=cited-filter]').text()).toContain(FEEDBACK.citedFilter('Altan och uteplats'))

    list.mockResolvedValue({ items: [], next: null })
    await wrapper.get('[data-test=clear-cited]').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ documentId: undefined }))
    expect(replace).toHaveBeenCalledWith({ query: { tab: 'x' } })
    expect(wrapper.find('[data-test=cited-filter]').exists()).toBe(false)
  })

  it('names no document while no row has loaded, and sends no document filter without ?documentId=', async () => {
    signIn(['rag-viewer'])
    route.query = { documentId: 'd9' }
    const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ lastUsedOn: null, totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()
    expect(wrapper.get('[data-test=cited-filter]').text()).toContain(FEEDBACK.citedFilter(null))

    route.query = {}
    const plain = mount(FeedbackView)
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ documentId: undefined }))
    expect(plain.find('[data-test=cited-filter]').exists()).toBe(false)
  })

  describe('the filter row (admin design §5.12 Filters, B-80)', () => {
    function mocks() {
      const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [item()], next: 'f9' as never })
      const summary = vi.spyOn(api.feedback, 'summary').mockResolvedValue({ lastUsedOn: null, totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
      vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
      return { list, summary }
    }

    it('is one row of chips, each named for its filter and showing its value, with a set chip\'s own × and Clear filters', async () => {
      signIn(['rag-viewer'])
      mocks()
      const wrapper = mount(FeedbackView, { attachTo: document.body })
      await flushPromises()
      const chips = wrapper.findAll('[data-test=chip-select]')
      expect(chips).toHaveLength(5)
      for (const c of chips) expect(wrapper.find(`label[for="${c.attributes('id')}"]`).exists()).toBe(true)
      const rating = wrapper.get('[data-test="chip-Rating"]')
      expect(rating.get('label').text()).toBe('Rating:')
      expect((rating.get('select').element as HTMLSelectElement).selectedOptions[0]!.text).toBe(FEEDBACK.allRatings)
      expect(rating.find('[data-test=chip-remove]').exists()).toBe(false)
      expect(wrapper.find('[data-test=clear-filters]').exists()).toBe(false)

      await rating.get('select').setValue('down')
      expect((rating.get('select').element as HTMLSelectElement).selectedOptions[0]!.text).toBe('Thumbs down')
      expect(rating.get('[data-test=chip-remove]').attributes('aria-label')).toBe(FILTERS.remove('Rating'))
      expect(rating.classes()).toContain('bg-accent-soft')
      await wrapper.get('[data-test=clear-filters]').trigger('click')
      expect((rating.get('select').element as HTMLSelectElement).value).toBe('')
      wrapper.unmount()
    })

    it('keeps the filters in the address: read on opening (a link, a reload) and written on a change without moving focus', async () => {
      signIn(['rag-viewer'])
      route.query = { period: '7d', rating: 'down', state: 'all' }
      const { list } = mocks()
      const wrapper = mount(FeedbackView, { attachTo: document.body })
      await flushPromises()
      expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ rating: '-1', state: undefined }))
      expect((wrapper.get('[data-test="chip-Rating"] select').element as HTMLSelectElement).value).toBe('down')
      expect((wrapper.get('[data-test="chip-Period"] select').element as HTMLSelectElement).value).toBe('7d')

      const outcome = wrapper.get('[data-test="chip-Answer"] select')
      ;(outcome.element as HTMLSelectElement).focus()
      await outcome.setValue('not-found')
      await flushPromises()
      expect(replace).toHaveBeenLastCalledWith({ query: { period: '7d', rating: 'down', state: 'all', outcome: 'not-found' } })
      expect(document.activeElement).toBe(outcome.element) // a change moves no focus

      // Back to the defaults: the keys leave the address.
      await wrapper.get('[data-test=clear-filters]').trigger('click')
      await flushPromises()
      expect(replace).toHaveBeenLastCalledWith({ query: {} })
      wrapper.unmount()
    })

    it('says the count at the end of the row in a polite live region', async () => {
      signIn(['rag-viewer'])
      mocks()
      const wrapper = mount(FeedbackView)
      await flushPromises()
      const count = wrapper.get('[data-test=filter-count]')
      expect(count.attributes('aria-live')).toBe('polite')
      expect(count.text()).toBe(FEEDBACK.shown(1, true))
    })

    it('offers the period presets, and the custom range as two labelled date fields', async () => {
      signIn(['rag-viewer'])
      const { list } = mocks()
      const wrapper = mount(FeedbackView)
      await flushPromises()
      const period = wrapper.get('[data-test="chip-Period"] select')
      expect(period.findAll('option').map((o) => o.text())).toEqual(['Last 30 days', 'Last 7 days', 'This month', 'Custom…'])
      expect(wrapper.find('[data-test=period-from]').exists()).toBe(false)
      await period.setValue('custom')
      expect(wrapper.get('[data-test=period-from]').text()).toContain(FILTERS.from)
      expect(wrapper.get('[data-test=period-to] input').attributes('type')).toBe('date')
      await wrapper.get('[data-test=period-from] input').setValue('2026-11-01')
      await wrapper.get('[data-test=period-from] input').trigger('change')
      await wrapper.get('[data-test=period-to] input').setValue('2026-11-10')
      await wrapper.get('[data-test=period-to] input').trigger('change')
      await flushPromises()
      expect(list).toHaveBeenLastCalledWith(expect.objectContaining({
        from: new Date('2026-11-01T00:00:00').toISOString(), to: new Date('2026-11-11T00:00:00').toISOString(),
      }))
    })
  })
})
