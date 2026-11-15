import type { Tone } from './labels'

/**
 * A run state's badge tone, the same on the source's runs, a publication and the knowledge base list: Done is ok, Failed is
 * danger, Running (or no run yet) is neutral, and the rest (Done with remarks, waiting for a deletion decision, cancelled)
 * is warn.
 */
export function runStateTone(state: string | undefined): Tone {
  return state === 'succeeded' ? 'ok' : state === 'failed' ? 'danger' : state === 'running' || !state ? 'neutral' : 'warn'
}
