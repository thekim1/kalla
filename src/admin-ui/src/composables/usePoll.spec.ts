import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope } from 'vue'
import { pollDelay, usePoll } from './usePoll'

describe('usePoll (admin design §4.7)', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('polls every 2 s, every 5 s after 30 s and every 15 s after 5 minutes', () => {
    expect([0, 29_999, 30_000, 299_999, 300_000].map(pollDelay)).toEqual([2_000, 2_000, 5_000, 5_000, 15_000])
  })

  it('ticks at once, then after each backoff step', async () => {
    const tick = vi.fn(async () => false)
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick))!
    poll.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(tick).toHaveBeenCalledTimes(1)

    // 2 s steps until 30 s have passed: ticks at 2, 4, ..., 30 s.
    await vi.advanceTimersByTimeAsync(30_000)
    expect(tick).toHaveBeenCalledTimes(16)
    // Then 5 s steps.
    await vi.advanceTimersByTimeAsync(4_999)
    expect(tick).toHaveBeenCalledTimes(16)
    await vi.advanceTimersByTimeAsync(1)
    expect(tick).toHaveBeenCalledTimes(17)
    expect(poll.state.value).toBe('polling')
    scope.stop()
  })

  it('waits one delay first with start(false)', async () => {
    const tick = vi.fn(async () => false)
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick, () => 1_000))!
    poll.start(false)
    await vi.advanceTimersByTimeAsync(999)
    expect(tick).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(tick).toHaveBeenCalledTimes(1)
    scope.stop()
  })

  it('stops when the tick says done', async () => {
    let calls = 0
    const tick = vi.fn(async () => ++calls === 2)
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick))!
    poll.start()
    await vi.advanceTimersByTimeAsync(2_000)
    expect(tick).toHaveBeenCalledTimes(2)
    expect(poll.state.value).toBe('done')
    expect(poll.polling.value).toBe(false)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(tick).toHaveBeenCalledTimes(2)
    scope.stop()
  })

  it('ends in failed with the error when a tick throws, and ticks no more', async () => {
    const failure = new Error('502')
    const tick = vi.fn(async (): Promise<boolean> => {
      throw failure
    })
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick))!
    poll.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(poll.state.value).toBe('failed')
    expect(poll.error.value).toBe(failure)
    expect(poll.polling.value).toBe(false)
    await vi.advanceTimersByTimeAsync(60_000)
    expect(tick).toHaveBeenCalledTimes(1)

    // A new start clears the error.
    tick.mockResolvedValueOnce(true)
    poll.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(poll.error.value).toBeNull()
    expect(poll.state.value).toBe('done')
    scope.stop()
  })

  it('ticks no more once its scope ends (the component unmounts)', async () => {
    const tick = vi.fn(async () => false)
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick))!
    poll.start()
    await vi.advanceTimersByTimeAsync(0)
    scope.stop()
    expect(poll.state.value).toBe('idle')
    await vi.advanceTimersByTimeAsync(60_000)
    expect(tick).toHaveBeenCalledTimes(1)
  })

  it('tells a tick whose answer came after a restart that it is no longer current, and ignores it', async () => {
    let release!: () => void
    const seen: boolean[] = []
    let calls = 0
    const tick = vi.fn(async (current: () => boolean) => {
      const mine = ++calls
      if (mine === 1) await new Promise<void>((resolve) => (release = resolve))
      seen.push(current())
      return mine === 1
    })
    const scope = effectScope()
    const poll = scope.run(() => usePoll(tick))!
    poll.start()
    poll.start()
    await vi.advanceTimersByTimeAsync(0)
    release()
    await vi.advanceTimersByTimeAsync(0)
    // The second poll is current and not done; the first one's late "done" changed nothing.
    expect(seen).toEqual([true, false])
    expect(poll.state.value).toBe('polling')
    scope.stop()
  })
})
