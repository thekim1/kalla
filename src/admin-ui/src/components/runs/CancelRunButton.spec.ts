import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Job } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import CancelRunButton from './CancelRunButton.vue'

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}
const dialogButton = (label: string) => [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === label).at(-1)!
const job = (state: string, lastError: string | null = null): Job => ({
  id: 'j1', class: 'command', kind: 'cancel-run', state, enqueuedAt: '2026-11-09T10:00:00Z', completedAt: null, runId: 'r1', result: null, lastError,
})

describe('Cancel run (#488 part 3)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it('confirms, queues cancel-run, waits for the command job and then reports the run cancelled', async () => {
    signIn(['rag-editor'])
    const cancel = vi.spyOn(api.runs, 'cancel').mockResolvedValue({ jobId: 'j1', runId: 'r1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job('succeeded'))
    const wrapper = mount(CancelRunButton, { props: { runId: 'r1', state: 'running' }, attachTo: document.body })

    await wrapper.get('[data-test=cancel-run]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Cancel this run?')
    expect(cancel).not.toHaveBeenCalled()

    dialogButton('Cancel run').click()
    await flushPromises()
    expect(cancel).toHaveBeenCalledWith('r1')
    expect(api.jobs.get).toHaveBeenCalledWith('j1')
    expect(wrapper.emitted('cancelled')).toHaveLength(1)
    wrapper.unmount()
  })

  it('shows run_not_active in the dialog when the run has already ended', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.runs, 'cancel').mockRejectedValue(new ApiError(409, 'POST', { code: 'run_not_active', params: { state: 'succeeded' } }))
    const wrapper = mount(CancelRunButton, { props: { runId: 'r1', state: 'awaiting-deletion-confirmation' }, attachTo: document.body })

    await wrapper.get('[data-test=cancel-run]').trigger('click')
    await flushPromises()
    dialogButton('Cancel run').click()
    await flushPromises()

    expect(document.body.textContent).toContain('The run has already ended')
    expect(wrapper.emitted('cancelled')).toBeUndefined()
    wrapper.unmount()
  })

  it('says why when the command job fails', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.runs, 'cancel').mockResolvedValue({ jobId: 'j1', runId: 'r1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job('failed', 'command.invalid_payload'))
    const wrapper = mount(CancelRunButton, { props: { runId: 'r1', state: 'running' }, attachTo: document.body })

    await wrapper.get('[data-test=cancel-run]').trigger('click')
    await flushPromises()
    dialogButton('Cancel run').click()
    await flushPromises()

    expect(wrapper.get('[data-test=cancel-run-error]').text()).toContain('The run could not be cancelled')
    expect(wrapper.emitted('cancelled')).toBeUndefined()
    wrapper.unmount()
  })

  it('is not offered on an ended run or to a reader', async () => {
    signIn(['rag-editor'])
    expect(mount(CancelRunButton, { props: { runId: 'r1', state: 'succeeded' } }).find('[data-test=cancel-run]').exists()).toBe(false)
    signIn(['rag-viewer'])
    expect(mount(CancelRunButton, { props: { runId: 'r1', state: 'running' } }).find('[data-test=cancel-run]').exists()).toBe(false)
  })
})
