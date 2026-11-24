import { isoDay, lastDays } from '@/components/costs/costs'

/** The period chip's presets (admin design §5.12 *Filters*). */
export const PERIODS = ['7d', '30d', 'month', 'custom'] as const
export type Period = (typeof PERIODS)[number]

/**
 * The days a period covers, as `YYYY-MM-DD`, both included: the last 7 or 30 days up to today, this month up to today,
 * or the custom range (either end may be empty: open; a backwards range is turned round). An unknown value reads as the
 * last 30 days.
 */
export function periodRange(period: string, from = '', to = '', today: Date = new Date()): { from: string; to: string } {
  if (period === '7d') return lastDays(7, today)
  if (period === 'month') return { from: isoDay(new Date(today.getFullYear(), today.getMonth(), 1)), to: isoDay(today) }
  // A range written backwards is read forwards, rather than sent as an error the API refuses.
  if (period === 'custom') return from && to && from > to ? { from: to, to: from } : { from, to }
  return lastDays(30, today)
}
