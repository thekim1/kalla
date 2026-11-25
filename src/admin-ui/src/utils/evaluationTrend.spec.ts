import { describe, expect, it } from 'vitest'
import type { EvaluationTrendRun } from '@/api/types'
import { trendChart, trendLayers } from './evaluationTrend'

const run = (startedAt: string, layers: EvaluationTrendRun['layers']): EvaluationTrendRun => ({
  id: startedAt, kind: 'nightly', variant: 'hybrid', startedAt, questionCount: 10, recallAtK: 0.8, mrr: 0.6, layers,
})

describe('The evaluation trend\'s data (admin design §7.1, B-69)', () => {
  const runs = [
    run('2026-11-01T02:00:00Z', [{ layer: 'generated', questions: 40, recallAtK: 0.9, mrr: 0.7 }]),
    run('2026-11-02T02:00:00Z', [{ layer: 'human', questions: 12, recallAtK: 0.75, mrr: 0.5 }, { layer: 'generated', questions: 40, recallAtK: 0.92, mrr: 0.71 }]),
  ]

  it('draws one line per metric and layer, recall then MRR, the human layer first, with a gap where a run has no layer', () => {
    const { slots, series } = trendChart(runs)
    expect(slots).toEqual([Date.parse('2026-11-01T02:00:00Z'), Date.parse('2026-11-02T02:00:00Z')])
    expect(series.map((s) => s.id)).toEqual(['human-recall', 'human-mrr', 'generated-recall', 'generated-mrr'])
    expect(series.map((s) => s.name)).toEqual([
      'From your organisation · Accuracy (recall@10)', 'From your organisation · MRR', 'Generated · Accuracy (recall@10)', 'Generated · MRR',
    ])
    expect(series[0]!.values).toEqual([null, 0.75])
    expect(series[3]!.values).toEqual([0.7, 0.71])
    expect(series.map((s) => s.color)).toEqual(['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-4)'])
  })

  it('gives runs started in the same millisecond distinct slots, and orders unknown layers after the known ones', () => {
    const same = [run('2026-11-03T02:00:00Z', []), { ...run('2026-11-03T02:00:00Z', []), id: 'b' }]
    expect(new Set(trendChart(same).slots).size).toBe(2)
    expect(trendLayers([run('2026-11-01T00:00:00Z', [
      { layer: 'skill', questions: 1, recallAtK: 1, mrr: 1 }, { layer: 'generated', questions: 1, recallAtK: 1, mrr: 1 }, { layer: 'human', questions: 1, recallAtK: 1, mrr: 1 },
    ])])).toEqual(['human', 'generated', 'skill'])
    expect(trendChart([]).series).toEqual([])
  })
})
