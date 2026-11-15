import { describe, expect, it, vi } from 'vitest'
import { allPages } from './clientChoices'

describe('allPages', () => {
  it('reads page after page until the total is read', async () => {
    const rows = Array.from({ length: 450 }, (_, i) => i)
    const read = vi.fn(async (page: number, pageSize: number) => ({ items: rows.slice((page - 1) * pageSize, page * pageSize), total: rows.length }))
    expect(await allPages(read)).toEqual(rows)
    expect(read.mock.calls).toEqual([[1, 200], [2, 200], [3, 200]])
  })

  it('stops at an empty page, so a total that shrinks while it reads cannot loop', async () => {
    const read = vi.fn(async (page: number) => ({ items: page === 1 ? ['a'] : [], total: 5 }))
    expect(await allPages(read)).toEqual(['a'])
    expect(read).toHaveBeenCalledTimes(2)
  })
})
