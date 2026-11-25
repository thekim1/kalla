import type { EvaluationTrendRun } from '@/api/types'
import type { ChartSeries } from '@/components/usage/TimeSeriesChart.vue'
import { EVALUATION } from '@/utils/labels'

/** The layers in the order the trend shows them (§7.1): the `human` gold set first, the generated regression signal next. */
const LAYER_ORDER = ['human', 'generated']

/** The trend's layers: those any run has, in `LAYER_ORDER`, then any other by name. */
export function trendLayers(runs: EvaluationTrendRun[]): string[] {
  const present = [...new Set(runs.flatMap((r) => r.layers.map((l) => l.layer)))]
  const rank = (layer: string) => (LAYER_ORDER.includes(layer) ? LAYER_ORDER.indexOf(layer) : LAYER_ORDER.length)
  return present.sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
}

/**
 * The chart's data (admin design §7.1, B-69): one slot per run, oldest first (two runs started in the same millisecond,
 * as an on-demand run's variants may be, get distinct slots), and one line per metric and layer, recall@10 then MRR,
 * the `human` layer first. A run without a layer leaves a gap in its lines. The colour follows the line's place, so a
 * layer keeps its colours from run to run.
 */
export function trendChart(runs: EvaluationTrendRun[]): { slots: number[]; series: ChartSeries[] } {
  const slots: number[] = []
  for (const run of runs) {
    const at = Date.parse(run.startedAt)
    const last = slots.at(-1)
    slots.push(last !== undefined && at <= last ? last + 1 : at)
  }
  const series = trendLayers(runs).flatMap((layer, n) => {
    const name = EVALUATION.layers[layer] ?? layer
    const of = (pick: (l: EvaluationTrendRun['layers'][number]) => number) =>
      runs.map((r) => {
        const found = r.layers.find((l) => l.layer === layer)
        return found ? pick(found) : null
      })
    return [
      { id: `${layer}-recall`, name: EVALUATION.trendSeries(name, EVALUATION.recall), color: `var(--series-${2 * n + 1})`, values: of((l) => l.recallAtK) },
      { id: `${layer}-mrr`, name: EVALUATION.trendSeries(name, EVALUATION.mrr), color: `var(--series-${2 * n + 2})`, values: of((l) => l.mrr) },
    ]
  })
  return { slots, series }
}
