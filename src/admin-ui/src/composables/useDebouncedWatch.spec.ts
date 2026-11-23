import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, ref } from 'vue'
import { useDebouncedWatch } from './useDebouncedWatch'

describe('useDebouncedWatch', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })
  afterEach(() => {
    vi.useRealTimers()
  })

  it('calls back once with the last value after it stood still for the delay', async () => {
    const search = ref('')
    const callback = vi.fn()
    const scope = effectScope()
    scope.run(() => useDebouncedWatch(search, callback, 300))

    search.value = 'k'
    await nextTick()
    await vi.advanceTimersByTimeAsync(200)
    search.value = 'kä'
    await nextTick()
    await vi.advanceTimersByTimeAsync(299)
    expect(callback).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(callback).toHaveBeenCalledTimes(1)
    expect(callback).toHaveBeenCalledWith('kä')
    scope.stop()
  })

  it('drops the pending call when its scope ends (the page is left), so nothing is fetched after', async () => {
    const search = ref('')
    const fetch = vi.fn()
    const scope = effectScope()
    scope.run(() => useDebouncedWatch(search, fetch, 300))

    search.value = 'källa'
    await nextTick()
    scope.stop()
    await vi.advanceTimersByTimeAsync(1_000)
    expect(fetch).not.toHaveBeenCalled()
  })
})
