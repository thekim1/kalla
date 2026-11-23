import { computed, getCurrentScope, onScopeDispose, ref } from 'vue'

/** The poll delay after `elapsedMs` (admin design §4.7): every 2 s, every 5 s after 30 s, every 15 s after 5 minutes. */
export function pollDelay(elapsedMs: number): number {
  if (elapsedMs < 30_000) return 2_000
  if (elapsedMs < 300_000) return 5_000
  return 15_000
}

/**
 * `idle` before `start` and after `stop`; `done` when a tick said so; `failed` when a tick threw (`error` holds why
 * until the next `start`).
 */
export type PollState = 'idle' | 'polling' | 'done' | 'failed'

/**
 * Calls `tick` until it answers true (done), waiting `delay(elapsed ms since start)` between calls (§4.7 by default).
 * A tick that throws ends the poll in `failed`. `start` replaces a running poll (by default the first tick runs at once;
 * `start(false)` waits one delay first); `stop` ends it, as does the end of the calling component or scope. `current()`
 * passed to the tick is false once its poll was stopped or replaced, so the tick can drop an answer that came too late.
 */
export function usePoll(tick: (current: () => boolean) => Promise<boolean>, delay: (elapsedMs: number) => number = pollDelay) {
  const state = ref<PollState>('idle')
  const error = ref<unknown>(null)
  const polling = computed(() => state.value === 'polling')
  let timer: ReturnType<typeof setTimeout> | null = null
  let generation = 0

  function stop(): void {
    if (timer) clearTimeout(timer)
    timer = null
    generation++
    state.value = 'idle'
  }

  function start(immediately = true): void {
    stop()
    const mine = generation
    const current = () => mine === generation
    const started = Date.now()
    error.value = null
    state.value = 'polling'
    const next = async () => {
      timer = null
      let done: boolean
      try {
        done = await tick(current)
      } catch (e) {
        if (!current()) return
        error.value = e
        state.value = 'failed'
        return
      }
      if (!current()) return
      if (done) state.value = 'done'
      else timer = setTimeout(next, delay(Date.now() - started))
    }
    if (immediately) void next()
    else timer = setTimeout(next, delay(0))
  }

  if (getCurrentScope()) onScopeDispose(stop)
  return { state, error, polling, start, stop }
}
