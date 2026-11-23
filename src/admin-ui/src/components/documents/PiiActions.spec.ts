import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Job } from '@/api/types'
import { PII_ACTIONS as L, PREVIEW } from '@/utils/labels'
import PiiActions from './PiiActions.vue'

const job = (id: string, state: string) => ({ id, state, class: 'preview', kind: null, enqueuedAt: '', completedAt: null, runId: null, result: null, lastError: null }) as Job

async function open() {
  const wrapper = mount(PiiActions, { props: { documentId: 'd1', title: 'Kontakt' }, attachTo: document.body })
  await flushPromises()
  return wrapper
}

describe('A flagged document\'s personal-data actions (admin design §5.5.2, §5.5.3, V26; B-63)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    setActivePinia(createPinia())
  })
  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('shows the highlights: the matches marked in the passages\' text, with their type, after the preview has run', async () => {
    vi.spyOn(api.documents, 'piiPreview').mockResolvedValue({ jobId: 'j1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValueOnce(job('j1', 'running')).mockResolvedValue(job('j1', 'succeeded'))
    const output = vi.spyOn(api.previews, 'output').mockImplementation(async (_job, _doc, stage) => (stage === 'chunk'
      ? { stage, truncated: false, value: { chunks: [
          { ordinal: 0, prefix: '', headingPath: '', text: 'Ingen träff här.', tokenCount: 3 },
          { ordinal: 1, prefix: '', headingPath: '', text: 'Ring 090-16 10 00 eller anna@example.com.', tokenCount: 8 },
        ] } }
      : { stage, truncated: false, value: { counts: {}, typesFired: [], state: 'held', spans: [{ ordinal: 1, type: 'phone', start: 5, length: 12 }, { ordinal: 1, type: 'emailExternal', start: 24, length: 17 }] } }) as never)
    vi.useFakeTimers()
    const wrapper = await open()
    await wrapper.get('[data-test=pii-highlights]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=pii-highlights-running]')!.textContent).toBe(L.running)
    await vi.advanceTimersByTimeAsync(2500)
    vi.useRealTimers()
    await flushPromises()

    expect(output).toHaveBeenCalledWith('j1', 'd1', 'chunk')
    expect(output).toHaveBeenCalledWith('j1', 'd1', 'personal-data')
    const list = document.body.querySelector('[data-test=pii-highlights-list]')!
    expect(list.querySelectorAll('li')).toHaveLength(1) // only the passage with a match
    const marks = [...list.querySelectorAll('mark')]
    expect(marks.map((m) => m.title)).toEqual([L.match(PREVIEW.piiTypes.phone!), L.match(PREVIEW.piiTypes.emailExternal!)])
    expect(marks[0]!.textContent).toBe(`${L.match(PREVIEW.piiTypes.phone!)}: 090-16 10 00`)
    expect(list.textContent).toContain('Ring ')
    wrapper.unmount()
  })

  it('says when nothing is found in the current text', async () => {
    vi.spyOn(api.documents, 'piiPreview').mockResolvedValue({ jobId: 'j1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job('j1', 'succeeded'))
    vi.spyOn(api.previews, 'output').mockResolvedValue({ stage: 'x', truncated: false, value: { chunks: [], spans: [] } } as never)
    const wrapper = await open()
    await wrapper.get('[data-test=pii-highlights]').trigger('click')
    await flushPromises()
    await flushPromises()
    expect(document.body.querySelector('[data-test=pii-highlights-none]')!.textContent).toBe(L.none)
    wrapper.unmount()
  })

  it('clears only with a reason of at least 10 characters, and tells the page when the clearance is done', async () => {
    const clear = vi.spyOn(api.documents, 'piiClear').mockResolvedValue({ jobId: 'c1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job('c1', 'succeeded'))
    const wrapper = await open()
    await wrapper.get('[data-test=pii-clear]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain(L.clearConsequence)

    const reason = document.body.querySelector<HTMLTextAreaElement>('#pii-clear-reason')!
    reason.value = '  för kort  '
    reason.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLFormElement>('#pii-clear-form')!.dispatchEvent(new Event('submit'))
    await flushPromises()
    expect(clear).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain(L.reasonTooShort)

    reason.value = 'Kontaktcentrets nummer är publicerat avsiktligt.'
    reason.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLFormElement>('#pii-clear-form')!.dispatchEvent(new Event('submit'))
    await flushPromises()
    await flushPromises()
    expect(clear).toHaveBeenCalledWith('d1', 'Kontaktcentrets nummer är publicerat avsiktligt.')
    expect(wrapper.emitted('cleared')).toHaveLength(1)
    wrapper.unmount()
  })
})
