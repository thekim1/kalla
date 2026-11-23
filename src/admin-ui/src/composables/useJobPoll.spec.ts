import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope } from 'vue'
import { api } from '@/api'
import type { Job } from '@/api/types'
import { useJobPoll } from './useJobPoll'

const job = (state: string): Job<unknown> => ({ id: 'j1', state } as Job<unknown>)

describe('useJobPoll (admin design §4.7)', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('polls the job until it finishes and says whether it failed', async () => {
    const get = vi.spyOn(api.jobs, 'get').mockResolvedValueOnce(job('running')).mockResolvedValueOnce(job('dead'))
    const scope = effectScope()
    const poll = scope.run(() => useJobPoll<unknown>())!
    poll.start('j1')
    await vi.advanceTimersByTimeAsync(0)
    expect(poll.job.value?.state).toBe('running')
    expect(poll.polling.value).toBe(true)
    expect(poll.failed.value).toBe(false)

    await vi.advanceTimersByTimeAsync(2_000)
    expect(poll.job.value?.state).toBe('dead')
    expect(poll.polling.value).toBe(false)
    expect(poll.failed.value).toBe(true)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(get).toHaveBeenCalledTimes(2)
    scope.stop()
  })

  it('a job that succeeded has not failed', async () => {
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job('succeeded'))
    const scope = effectScope()
    const poll = scope.run(() => useJobPoll<unknown>())!
    poll.start('j1')
    await vi.advanceTimersByTimeAsync(0)
    expect(poll.polling.value).toBe(false)
    expect(poll.failed.value).toBe(false)
    scope.stop()
  })

  it('a failed request ends the poll in failed, with the error and no job, instead of running for ever', async () => {
    const failure = new Error('502')
    const get = vi.spyOn(api.jobs, 'get').mockRejectedValue(failure)
    const scope = effectScope()
    const poll = scope.run(() => useJobPoll<unknown>())!
    poll.start('j1')
    await vi.advanceTimersByTimeAsync(0)
    expect(poll.job.value).toBeNull()
    expect(poll.error.value).toBe(failure)
    expect(poll.polling.value).toBe(false)
    expect(poll.failed.value).toBe(true)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(get).toHaveBeenCalledTimes(1)
    scope.stop()
  })

  it('fetches nothing after the component unmounts', async () => {
    const get = vi.spyOn(api.jobs, 'get').mockResolvedValue(job('running'))
    const scope = effectScope()
    const poll = scope.run(() => useJobPoll<unknown>())!
    poll.start('j1')
    await vi.advanceTimersByTimeAsync(0)
    scope.stop()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(get).toHaveBeenCalledTimes(1)
  })
})
