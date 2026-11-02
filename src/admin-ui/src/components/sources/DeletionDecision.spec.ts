import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import DeletionDecision from './DeletionDecision.vue'
import HeldDocuments from './HeldDocuments.vue'

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}
const dialogButton = (label: string) => [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === label).at(-1)!

describe('Borttagning väntar på beslut and Behållna (admin design §5.5.3, connectors §13)', () => {
  it('lists the proposals and confirms with the count the user saw; a newer run answers deletion_superseded', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.runs, 'deletions').mockResolvedValue({
      items: [{ documentId: 'd1', title: 'Altan', url: 'https://www.umea.se/bygga/altan', reason: 'http.404', exempt: false, decision: null, lastSeenAt: '2026-10-23T10:00:00Z' }],
      next: null,
    })
    const confirm = vi.spyOn(api.runs, 'confirmDeletions')
      .mockRejectedValueOnce(new ApiError(409, 'POST', { code: 'deletion_superseded' }))
      .mockResolvedValueOnce({ jobId: 'j' })
    const wrapper = mount(DeletionDecision, { props: { runId: 'r1', proposedCount: 212 }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.text()).toContain('212 dokument hittades inte')
    expect(wrapper.text()).toContain('https://www.umea.se/bygga/altan')
    expect(wrapper.get('a[download]').attributes('href')).toBe('/api/runs/r1/deletions?format=csv')

    await wrapper.get('[data-test=remove]').trigger('click')
    await flushPromises()
    dialogButton('Ta bort 212 dokument').click()
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith('r1', 212)
    expect(document.body.textContent).toContain('Förslaget om borttagning gäller inte längre.')
    expect(wrapper.emitted('decided')).toBeUndefined()

    dialogButton('Ta bort 212 dokument').click()
    await flushPromises()
    expect(wrapper.emitted('decided')).toHaveLength(1)
    wrapper.unmount()
  })

  it('keeps the documents (reject), and an editor sees no decision buttons', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.runs, 'deletions').mockResolvedValue({ items: [], next: null })
    const reject = vi.spyOn(api.runs, 'rejectDeletions').mockResolvedValue({ jobId: 'j' })
    const wrapper = mount(DeletionDecision, { props: { runId: 'r1', proposedCount: 7 }, attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-test=keep]').trigger('click')
    await flushPromises()
    dialogButton('Behåll dokumenten').click()
    await flushPromises()
    expect(reject).toHaveBeenCalledWith('r1', 7)
    wrapper.unmount()

    signIn(['rag-editor'])
    const readOnly = mount(DeletionDecision, { props: { runId: 'r1', proposedCount: 7 } })
    await flushPromises()
    expect(readOnly.find('[data-test=keep]').exists()).toBe(false)
  })

  it('lists held documents and releases them after a confirmation', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.sources, 'held').mockResolvedValue({
      items: [{ id: 'd1', title: 'Altan', url: null, displayName: null, heldAt: '2026-10-30T10:00:00Z', heldBy: 'anna', lastSeenAt: '2026-10-23T10:00:00Z' }],
      next: null,
    })
    const release = vi.spyOn(api.sources, 'releaseHeld').mockResolvedValue({ jobId: 'j' })
    const wrapper = mount(HeldDocuments, { props: { sourceId: 's1' }, attachTo: document.body })
    await flushPromises()
    expect(wrapper.text()).toContain('Altan')
    expect(wrapper.text()).toContain('av anna')
    await wrapper.get('[data-test=release]').trigger('click')
    await flushPromises()
    dialogButton('Släpp alla').click()
    await flushPromises()
    expect(release).toHaveBeenCalledWith('s1')
    wrapper.unmount()
  })
})
