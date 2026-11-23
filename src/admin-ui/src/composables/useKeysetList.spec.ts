import { describe, expect, it, vi } from 'vitest'
import type { KeysetPage } from '@/api/types'
import { useKeysetList } from './useKeysetList'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useKeysetList', () => {
  it('reads the first page without a cursor', async () => {
    const fetchPage = vi.fn(async (): Promise<KeysetPage<string>> => ({ items: ['a', 'b'], next: 'b' }))
    const list = useKeysetList(fetchPage)
    expect(list.items.value).toEqual([])
    expect(list.next.value).toBeNull()

    await list.reload()
    expect(fetchPage).toHaveBeenCalledWith(undefined)
    expect(list.items.value).toEqual(['a', 'b'])
    expect(list.next.value).toBe('b')
    expect(list.loaded.value).toBe(true)
    expect(list.loading.value).toBe(false)
  })

  it('appends the next page with more() and does nothing at the end', async () => {
    const fetchPage = vi.fn(async (after?: string): Promise<KeysetPage<string>> =>
      after ? { items: ['c'], next: null } : { items: ['a', 'b'], next: 'b' })
    const list = useKeysetList(fetchPage)
    await list.reload()
    await list.more()
    expect(fetchPage).toHaveBeenLastCalledWith('b')
    expect(list.items.value).toEqual(['a', 'b', 'c'])
    expect(list.next.value).toBeNull()

    expect(await list.more()).toBeNull()
    expect(fetchPage).toHaveBeenCalledTimes(2)
  })

  it('keeps the rows and the cursor of the newest call when an older call ends later', async () => {
    const slow = deferred<KeysetPage<string>>()
    const fast = deferred<KeysetPage<string>>()
    const fetchPage = vi.fn().mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise)
    const list = useKeysetList<KeysetPage<string>>(fetchPage)
    const first = list.reload()
    const second = list.reload()

    fast.resolve({ items: ['new'], next: 'new-cursor' })
    await second
    slow.resolve({ items: ['old'], next: 'old-cursor' })
    expect(await first).toBeNull()
    expect(list.items.value).toEqual(['new'])
    expect(list.next.value).toBe('new-cursor')
    expect(list.loading.value).toBe(false)
  })

  it('starts again from the first page with reload()', async () => {
    const fetchPage = vi.fn(async (after?: string): Promise<KeysetPage<string>> =>
      after ? { items: ['c'], next: 'c' } : { items: ['a'], next: 'a' })
    const list = useKeysetList(fetchPage)
    await list.reload()
    await list.more()
    expect(list.items.value).toEqual(['a', 'c'])

    await list.reload()
    expect(fetchPage).toHaveBeenLastCalledWith(undefined)
    expect(list.items.value).toEqual(['a'])
    expect(list.next.value).toBe('a')
  })

  it('surfaces an error and keeps the rows already shown', async () => {
    const failure = new Error('down')
    const fetchPage = vi.fn(async (after?: string): Promise<KeysetPage<string>> => {
      if (after) throw failure
      return { items: ['a'], next: 'a' }
    })
    const list = useKeysetList(fetchPage)
    await list.reload()
    expect(await list.more()).toBeNull()
    expect(list.error.value).toBe(failure)
    expect(list.items.value).toEqual(['a'])
    expect(list.next.value).toBe('a')

    await list.reload()
    expect(list.error.value).toBeNull()
  })

  it('patches rows in place through items and keeps the page\'s other fields', async () => {
    const list = useKeysetList(async () => ({ items: ['a', 'b'], next: null, guard: 3 }))
    await list.reload()
    list.items.value = list.items.value.map((x) => x.toUpperCase())
    expect(list.items.value).toEqual(['A', 'B'])
    expect(list.data.value?.guard).toBe(3)
  })
})
