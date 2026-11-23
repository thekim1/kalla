import { onScopeDispose, watch, type WatchSource } from 'vue'

/**
 * Watches `source` and calls `callback` with its value once it has stood still for `delayMs` (a search field, filters).
 * A change within the delay restarts it. The pending call is dropped when the calling component or scope ends, so
 * nothing loads after the page is left.
 */
export function useDebouncedWatch<T>(source: WatchSource<T>, callback: (value: T) => void, delayMs: number): void {
  let timer: ReturnType<typeof setTimeout> | null = null
  const cancel = () => {
    if (timer) clearTimeout(timer)
    timer = null
  }
  watch(source, (value) => {
    cancel()
    timer = setTimeout(() => {
      timer = null
      callback(value)
    }, delayMs)
  })
  onScopeDispose(cancel)
}
