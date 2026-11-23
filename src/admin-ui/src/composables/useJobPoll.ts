import { computed, ref } from 'vue'
import { api } from '@/api'
import type { Job } from '@/api/types'
import { usePoll } from './usePoll'

/** Job states after which polling stops (admin design §4.7). */
export const FINISHED_STATES = new Set(['succeeded', 'failed', 'dead', 'cancelled'])

/**
 * Polls `GET /api/jobs/{id}` until the job finishes (§4.7 delays). `job` holds the latest state; `start` replaces a
 * running poll; a request error ends the poll with `error` set. `failed` says the poll ended without the job
 * succeeding: a request failed (`job` may still be null) or the job ended failed, dead or cancelled. Stops when the
 * component unmounts.
 */
export function useJobPoll<T>() {
  const job = ref<Job<T> | null>(null)
  let id = ''
  const poll = usePoll(async (current) => {
    const latest = await api.jobs.get<T>(id)
    if (!current()) return true
    job.value = latest
    return FINISHED_STATES.has(latest.state)
  })
  // From the job itself, so `job`, `polling` and `failed` change together when it finishes.
  const finished = computed(() => !!job.value && FINISHED_STATES.has(job.value.state))
  const polling = computed(() => poll.polling.value && !finished.value)
  const failed = computed(() => poll.state.value === 'failed' || (finished.value && job.value?.state !== 'succeeded'))

  function start(jobId: string): void {
    id = jobId
    job.value = null
    poll.start()
  }

  return { job, error: poll.error, polling, failed, start, stop: poll.stop }
}
