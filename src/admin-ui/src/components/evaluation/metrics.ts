import type { EvaluationResult } from '@/api/types'

/** One layer of a run, computed from its per-question results as the worker computes the run's totals (processing §13.6). */
export interface LayerMetrics {
  /** All questions of the layer in the run, answerable or not. */
  questions: number
  /** Questions with expected documents: the denominator of recall@k. */
  answerable: number
  found: number
  recall: number | null
  mrr: number | null
}

/** recall@k and MRR@k over the layer's answerable questions (`hit` is null for an unanswerable one). */
export function layerMetrics(results: EvaluationResult[], layer: string, k: number): LayerMetrics {
  const of = results.filter((r) => r.layer === layer)
  const answerable = of.filter((r) => r.hit !== null)
  const found = answerable.filter((r) => r.hit).length
  const reciprocal = answerable.reduce((sum, r) => sum + (r.rank != null && r.rank <= k ? 1 / r.rank : 0), 0)
  return {
    questions: of.length,
    answerable: answerable.length,
    found,
    recall: answerable.length ? found / answerable.length : null,
    mrr: answerable.length ? reciprocal / answerable.length : null,
  }
}

/**
 * Answerable questions in both runs: found now and not then (better), and the reverse (worse), as the worker counts
 * wins and losses (§13.6). Questions added or retired between the runs count in neither.
 */
export function rankChanges(results: EvaluationResult[], previous: EvaluationResult[] | null) {
  const before = new Map((previous ?? []).map((r) => [r.questionId, r]))
  const worse: EvaluationResult[] = []
  const better: EvaluationResult[] = []
  for (const r of results) {
    const then = before.get(r.questionId)
    if (r.hit === null || !then || then.hit === null) continue
    if (r.hit && !then.hit) better.push(r)
    else if (!r.hit && then.hit) worse.push(r)
  }
  return { worse, better }
}
