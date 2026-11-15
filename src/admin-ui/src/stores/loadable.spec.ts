import { describe, expect, it } from 'vitest'
import { useLoadable } from './loadable'

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('useLoadable', () => {
  it('keeps the newest call\'s answer when an older call ends later (#710)', async () => {
    const list = useLoadable<string[]>([])
    const older = deferred<string[]>()
    const newer = deferred<string[]>()
    const first = list.run(() => older.promise)
    const second = list.run(() => newer.promise)

    newer.resolve(['new'])
    expect(await second).toEqual(['new'])
    expect(list.loading.value).toBe(false)
    older.resolve(['old'])
    expect(await first).toBeNull()
    expect(list.data.value).toEqual(['new'])
    expect(list.loading.value).toBe(false)
  })

  it('ignores an older call\'s error and stays loading until the newest call ends', async () => {
    const item = useLoadable<string | null>(null)
    const older = deferred<string>()
    const newer = deferred<string>()
    const first = item.run(() => older.promise)
    const second = item.run(() => newer.promise)

    older.reject(new Error('slow and failed'))
    expect(await first).toBeNull()
    expect(item.error.value).toBeNull()
    expect(item.loading.value).toBe(true)
    newer.resolve('fresh')
    await second
    expect(item.data.value).toBe('fresh')
    expect(item.loaded.value).toBe(true)
  })

  it('keeps the error of the newest call', async () => {
    const item = useLoadable<string | null>(null)
    const failure = new Error('down')
    expect(await item.run(() => Promise.reject(failure))).toBeNull()
    expect(item.error.value).toBe(failure)
    expect(item.loading.value).toBe(false)
  })
})
