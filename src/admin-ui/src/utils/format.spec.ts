import { describe, expect, it } from 'vitest'
import { actorText, formatDate, formatDateTime, formatDateTimeToday, formatDecimal, formatRelative, formatUsdCost, lastDaysIso } from './format'

describe('formatUsdCost', () => {
  it('keeps up to four decimals under one dollar, so small costs are visible', () => {
    expect(formatUsdCost(0.0035)).toBe('0,0035 US$')
    expect(formatUsdCost(0.125)).toBe('0,125 US$')
    expect(formatUsdCost(48210.3)).toBe('48 210,30 US$')
    expect(formatUsdCost(null)).toBe('—')
  })
})

describe('formatDecimal', () => {
  it('formats decimals in the one number locale', () => {
    expect(formatDecimal(0.8, 2, 2)).toBe('0,80')
    expect(formatDecimal(1234.56789, 4)).toBe('1 234,5679')
  })
})

describe('lastDaysIso', () => {
  it('lists calendar days up to today, oldest first, also across a month end and a clock change', () => {
    expect(lastDaysIso(3, new Date('2026-11-01T10:00:00Z'))).toEqual(['2026-10-30', '2026-10-31', '2026-11-01'])
    expect(lastDaysIso(2, new Date('2026-03-30T00:30:00+02:00'))).toEqual(['2026-03-29', '2026-03-30'])
    expect(lastDaysIso(30, new Date('2026-10-24T12:00:00Z'))).toHaveLength(30)
  })
})

describe('dates as the gateway (admin design §5.7)', () => {
  it('writes dates as 30 Oct 2026 and times 24 h in Stockholm', () => {
    expect(formatDate('2026-10-30T12:00:00Z')).toBe('30 Oct 2026')
    expect(formatDate('2026-09-30T12:00:00Z')).toBe('30 Sep 2026')
    expect(formatDateTime('2026-10-30T13:05:00Z')).toBe('30 Oct 2026, 14:05')
  })

  it('says relative times in plain English', () => {
    const now = new Date('2026-10-30T12:00:00Z')
    expect(formatRelative('2026-10-30T11:58:00Z', now)).toBe('2 min ago')
    expect(formatRelative('2026-10-29T12:00:00Z', now)).toBe('Yesterday')
  })
})

describe('formatDateTimeToday (admin design §5.7)', () => {
  it('drops the date for a time on today\'s Stockholm date and keeps it otherwise', () => {
    const now = new Date('2026-11-23T12:00:00Z')
    expect(formatDateTimeToday('2026-11-23T00:17:00Z', now)).toBe('Today, 01:17')
    expect(formatDateTimeToday('2026-11-22T22:30:00Z', now)).toBe('22 Nov 2026, 23:30') // 23:30 in Stockholm on the 22nd
    expect(formatDateTimeToday(null, now)).toBe('—')
  })
})

describe('actorText (admin design §5.7)', () => {
  it('reads the demo seed as Demo data, prefers a person\'s name and keeps any other id', () => {
    expect(actorText('system:seed')).toBe('Demo data')
    expect(actorText('oid:1', 'Anna Andersson')).toBe('Anna Andersson')
    expect(actorText('system:retention')).toBe('system:retention')
    expect(actorText(null)).toBe('')
  })
})
