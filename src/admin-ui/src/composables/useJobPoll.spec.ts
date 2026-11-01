import { describe, expect, it } from 'vitest'
import { pollDelay } from './useJobPoll'

describe('job polling (admin design §4.7)', () => {
  it('polls every 2 s, every 5 s after 30 s and every 15 s after 5 minutes', () => {
    expect([0, 29_999, 30_000, 299_999, 300_000].map(pollDelay)).toEqual([2_000, 2_000, 5_000, 5_000, 15_000])
  })
})
