import { onBeforeUnmount, ref } from 'vue'
import { api } from '@/api'
import type { Job } from '@/api/types'

/** Job states after which polling stops (admin design §4.7). */
export const FINISHED_STATES = new Set(['succeeded', 'failed', 'dead', 'cancelled'])

/** The poll delay after `elapsedMs` (admin design §4.7): every 2 s, every 5 s after 30 s, every 15 s after 5 minutes. */
export function pollDelay(elapsedMs: number): number {
  if (elapsedMs < 30_000) return 2_000
  if (elapsedMs < 300_000) return 5_000
  return 15_000
}

/**
 * Polls `GET /api/jobs/{id}` until the job finishes. `job` holds the latest state; `start` replaces a running poll; a
 * request error ends the poll with `error` set. Stops when the component unmounts.
 */
export function useJobPoll<T>() {
  const job = ref<Job<T> | null>(null)
  const error = ref<unknown>(null)
  const polling = ref(false)
  let timer: ReturnType<typeof setTimeout> | null = null
  let generation = 0

  function stop(): void {
    if (timer) clearTimeout(timer)
    timer = null
    polling.value = false
    generation++
  }

  function start(id: string): void {
    stop()
    const mine = generation
    const started = Date.now()
    job.value = null
    error.value = null
    polling.value = true
    const tick = async () => {
      try {
        const current = await api.jobs.get<T>(id)
        if (mine !== generation) return
        job.value = current
        if (FINISHED_STATES.has(current.state)) {
          polling.value = false
          return
        }
      } catch (e) {
        if (mine !== generation) return
        error.value = e
        polling.value = false
        return
      }
      timer = setTimeout(tick, pollDelay(Date.now() - started))
    }
    void tick()
  }

  onBeforeUnmount(stop)
  return { job, error, polling, start, stop }
}
