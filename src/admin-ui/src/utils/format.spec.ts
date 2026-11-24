import { describe, expect, it } from 'vitest'
import { actorText, formatDate, formatDateTime, formatDateTimeToday, formatDecimal, formatRelative, formatUsdCost, lastDaysIso, partOfDay, quietZero } from './format'

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

describe('quietZero (admin design §5.12 rule 8)', () => {
  it('greys a zero or a missing count and leaves any other number as it is', () => {
    expect([quietZero(0), quietZero(null), quietZero(undefined)]).toEqual(['text-fg-3', 'text-fg-3', 'text-fg-3'])
    expect([quietZero(1), quietZero(-2), quietZero(0.5)]).toEqual([undefined, undefined, undefined])
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

describe('partOfDay (the greeting, admin design §5.7; #1036)', () => {
  it('takes the hour in Europe/Stockholm: morning from 05, afternoon from 12, evening from 18 until 05', () => {
    // November: Stockholm is UTC+1.
    expect(partOfDay(new Date('2026-11-24T03:59:00Z'))).toBe('evening') // 04:59
    expect(partOfDay(new Date('2026-11-24T04:00:00Z'))).toBe('morning') // 05:00
    expect(partOfDay(new Date('2026-11-24T10:59:00Z'))).toBe('morning') // 11:59
    expect(partOfDay(new Date('2026-11-24T11:00:00Z'))).toBe('afternoon') // 12:00
    expect(partOfDay(new Date('2026-11-24T16:59:00Z'))).toBe('afternoon') // 17:59
    expect(partOfDay(new Date('2026-11-24T17:00:00Z'))).toBe('evening') // 18:00
    expect(partOfDay(new Date('2026-11-24T23:30:00Z'))).toBe('evening') // 00:30
    // Summer time: UTC+2, so 03:00 UTC is 05:00 in Stockholm, morning; under UTC+1 it would be 04:00, evening (#1049).
    expect(partOfDay(new Date('2026-07-01T03:00:00Z'))).toBe('morning')
    expect(partOfDay(new Date('2026-07-01T10:00:00Z'))).toBe('afternoon') // 12:00 in summer, 11:00 under UTC+1
  })
})
