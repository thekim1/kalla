import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { FeedbackItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { FEEDBACK } from '@/utils/labels'
import FeedbackView from './FeedbackView.vue'

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

afterEach(() => vi.restoreAllMocks())

describe('Feedback (admin design §8.1)', () => {
  it('shows ratings without content, the totals and import health, filters, and marks an item handled', async () => {
    signIn(['rag-department-admin'])
    const list = vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [item(), item({ id: 'f2', rating: 1, reasonCategory: null, outcome: 'not-found', citedDocuments: [] })], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({
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

    await wrapper.findAll('select')[1]!.setValue('-1')
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
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
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
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
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
    await wrapper.findAll('select')[1]!.setValue('-1')
    await flushPromises()
    expect(wrapper.text()).toContain('Altan och uteplats')
  })

  it('labels every filter above its field, the same layout as Costs', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.feedback, 'list').mockResolvedValue({ items: [], next: null })
    vi.spyOn(api.feedback, 'summary').mockResolvedValue({ totals: { answers: 0, notFound: 0, thumbsUp: 0, thumbsDown: 0 }, imports: [] })
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 200 } as never)
    const wrapper = mount(FeedbackView)
    await flushPromises()

    const form = wrapper.get('form[role=search]')
    const fields = form.findAll('input, select')
    expect(fields).toHaveLength(6)
    for (const f of fields) expect(form.find(`label[for="${f.attributes('id')}"]`).exists()).toBe(true)
  })
})
