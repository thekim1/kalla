import { describe, expect, it } from 'vitest'
import { formatDate, formatDateTime, formatRelative, formatUsdCost, lastDaysIso } from './format'

describe('formatUsdCost', () => {
  it('keeps up to four decimals under one dollar, so small costs are visible', () => {
    expect(formatUsdCost(0.0035)).toBe('0,0035\u00A0USD')
    expect(formatUsdCost(0.125)).toBe('0,125\u00A0USD')
    expect(formatUsdCost(48210.3)).toBe('48\u00A0210,30\u00A0USD')
    expect(formatUsdCost(null)).toBe('—')
  })
})

describe('lastDaysIso', () => {
  it('lists calendar days up to today, oldest first, also across a month end and a clock change', () => {
    expect(lastDaysIso(3, new Date('2026-11-01T10:00:00Z'))).toEqual(['2026-10-30', '2026-10-31', '2026-11-01'])
    expect(lastDaysIso(2, new Date('2026-03-30T00:30:00+02:00'))).toEqual(['2026-03-29', '2026-03-30'])
    expect(lastDaysIso(30, new Date('2026-10-24T12:00:00Z'))).toHaveLength(30)
  })
})

describe('Swedish dates (admin design §5.7)', () => {
  it('writes dates as 30 okt. 2026 and times 24 h in Stockholm', () => {
    expect(formatDate('2026-10-30T12:00:00Z')).toBe('30 okt. 2026')
    expect(formatDateTime('2026-10-30T13:05:00Z')).toBe('30 okt. 2026 14:05')
  })

  it('says relative times in Swedish', () => {
    const now = new Date('2026-10-30T12:00:00Z')
    expect(formatRelative('2026-10-30T11:58:00Z', now)).toBe('för 2 min sedan')
    expect(formatRelative('2026-10-29T12:00:00Z', now)).toBe('I går')
  })
})
