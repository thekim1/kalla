import type { RunSummary } from '@/api/types'
import { formatNumber } from './format'
import { RUN_PROGRESS, type Tone } from './labels'

/**
 * A run state's badge tone, the same on the source's runs, a publication and the knowledge base list: Done is ok, Failed is
 * danger, Running (or no run yet) is neutral, and the rest (Done with remarks, waiting for a deletion decision, cancelled)
 * is warn.
 */
export function runStateTone(state: string | undefined): Tone {
  return state === 'succeeded' ? 'ok' : state === 'failed' ? 'danger' : state === 'running' || !state ? 'neutral' : 'warn'
}

/**
 * A running run's progress as one line (#803): the phase, then what was handled, what waits and what the crawl found
 * ("Fetching and processing pages · 120 handled · 80 waiting · 200 found (limit 5 000)"); null when the run has no
 * progress (it is not running).
 */
export function runProgressText(run: RunSummary | undefined): string | null {
  const p = run?.progress
  if (!run || !p) return null
  const parts = [
    ...(run.phase && RUN_PROGRESS.phase[run.phase] ? [RUN_PROGRESS.phase[run.phase]] : []),
    RUN_PROGRESS.handled(formatNumber(p.processed)),
    ...(p.waiting != null ? [RUN_PROGRESS.waiting(formatNumber(p.waiting))] : []),
    ...(p.admitted != null
      ? [p.maxPages ? RUN_PROGRESS.foundOfLimit(formatNumber(p.admitted), formatNumber(p.maxPages)) : RUN_PROGRESS.found(formatNumber(p.admitted))]
      : []),
  ]
  return parts.join(' · ')
}
