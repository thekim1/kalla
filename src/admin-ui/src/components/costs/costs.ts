import type { CostTotals } from '@/api/types'
import { COSTS } from '@/utils/labels'

/** The steps that are a knowledge base's indexing (admin design §8.6); the rest of its steps are evaluation. */
export const INDEXING_STEPS: readonly string[] = ['embed']

/** The client row of the test panel (admin design §4.9). */
export const TEST_PANEL_CLIENT = 'admin-console'

/** A step's name (labels.ts), or its code for a step without one (P2 agent steps). */
export function stepLabel(step: string): string {
  return COSTS.steps[step] ?? step
}

/** A client's name, the test panel's as *Test panel*. */
export function clientLabel(name: string | null): string {
  return name === TEST_PANEL_CLIENT ? COSTS.testPanel : name ?? ''
}

/** The gateway's cost plus the estimate. */
export function totalCost(t: Pick<CostTotals, 'costUsd' | 'estimatedCostUsd'>): number {
  return t.costUsd + t.estimatedCostUsd
}

export function emptyTotals(): CostTotals {
  return { calls: 0, inputTokens: 0, cachedInputTokens: 0, outputTokens: 0, costUsd: 0, estimatedCostUsd: 0, estimatedCalls: 0 }
}

/** The sum of `rows`' fields. */
export function sumTotals(rows: readonly CostTotals[]): CostTotals {
  const sum = emptyTotals()
  for (const r of rows) {
    sum.calls += r.calls
    sum.inputTokens += r.inputTokens
    sum.cachedInputTokens += r.cachedInputTokens
    sum.outputTokens += r.outputTokens
    sum.costUsd += r.costUsd
    sum.estimatedCostUsd += r.estimatedCostUsd
    sum.estimatedCalls += r.estimatedCalls
  }
  return sum
}

/** `rows` summed per `key`, highest total cost first. */
export function groupTotals<R extends CostTotals>(rows: readonly R[], key: (row: R) => string): { key: string; totals: CostTotals }[] {
  const groups = new Map<string, R[]>()
  for (const r of rows) {
    const k = key(r)
    const group = groups.get(k)
    if (group) group.push(r)
    else groups.set(k, [r])
  }
  return [...groups.entries()].map(([k, rs]) => ({ key: k, totals: sumTotals(rs) })).sort((a, b) => totalCost(b.totals) - totalCost(a.totals) || a.key.localeCompare(b.key))
}

/** A local date as `yyyy-MM-dd`. */
export function isoDay(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

/** The last `days` days, today included, as inclusive dates. */
export function lastDays(days: number, today: Date = new Date()): { from: string; to: string } {
  const from = new Date(today)
  from.setDate(from.getDate() - (days - 1))
  return { from: isoDay(from), to: isoDay(today) }
}
