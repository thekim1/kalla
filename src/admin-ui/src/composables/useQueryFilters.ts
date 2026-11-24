import { computed, nextTick, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

/** The address' days: `YYYY-MM-DD`. */
export const IS_DAY = (v: string) => /^\d{4}-\d{2}-\d{2}$/.test(v)
/** An id the API takes (a Guid). */
export const IS_ID = (v: string) => /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v)
/** One of these values. */
export const ONE_OF = (values: readonly string[]) => (v: string) => values.includes(v)

/**
 * A page's filters kept in the address (admin design §5.12 *Filters*): `?period=30d&rating=down`, so a filtered view can
 * be linked, survives a reload and comes back with back and forward. Each key has a default; a key at its default is
 * left out of the address. A change replaces the query (no history entry, no reload, focus stays where it is), and a
 * change of the address (a link, back, forward) is followed.
 *
 * `valid` checks a key's value from the address: anything else reads as the default, so `?period=foo` or a bad id in
 * `?assistant=` is not sent to the API. With a `period` key, `from` and `to` belong to its *Custom…* range: they are
 * reset (and so leave the address and the set filters) whenever the period is not `custom`.
 */
export function useQueryFilters<K extends string>(defaults: Record<K, string>, valid: Partial<Record<K, (value: string) => boolean>> = {}) {
  const route = useRoute()
  const router = useRouter()
  const keys = Object.keys(defaults) as K[]
  const hasRange = (['period', 'from', 'to'] as string[]).every((k) => keys.includes(k as K))
  const rangeKey = (k: K) => hasRange && (k === 'from' || k === 'to')

  const read = (): Record<K, string> => {
    const raw = Object.fromEntries(keys.map((k) => {
      const value = route?.query[k]
      const ok = typeof value === 'string' && (valid[k]?.(value) ?? true)
      return [k, ok ? value : defaults[k]]
    })) as Record<K, string>
    if (hasRange && raw['period' as K] !== 'custom') for (const k of keys) if (rangeKey(k)) raw[k] = defaults[k]
    return raw
  }
  const filters = reactive(read()) as Record<K, string>

  // Leaving Custom… drops its range, so no stale from/to stays set while every chip looks unset.
  if (hasRange) {
    watch(() => filters['period' as K], (period) => {
      if (period === 'custom') return
      for (const k of keys) if (rangeKey(k)) filters[k] = defaults[k]
    })
  }

  watch(() => ({ ...filters }), (now) => {
    if (!route || !router?.replace) return
    const query: Record<string, unknown> = { ...route.query }
    let changed = false
    for (const k of keys) {
      const want = now[k] === defaults[k] ? undefined : now[k]
      if ((query[k] ?? undefined) === want) continue
      changed = true
      if (want === undefined) delete query[k]
      else query[k] = want
    }
    if (changed) void router.replace({ query: query as Record<string, string> })
  })
  watch(() => keys.map((k) => route?.query[k]), () => {
    const next = read()
    for (const k of keys) if (filters[k] !== next[k]) filters[k] = next[k]
  })

  /** The filters that are set (not at their default). */
  const active = computed(() => keys.filter((k) => filters[k] !== defaults[k]))
  /**
   * Clear filters: every key back to its default. The button goes with the last set filter, so focus moves to the first
   * chip of `row` (the filter row), never to the body (§5.12 *Filters*, WCAG 2.4.3).
   */
  function clear(row?: HTMLElement | null): void {
    for (const k of keys) filters[k] = defaults[k]
    if (row) void nextTick(() => row.querySelector<HTMLElement>('[data-test=chip-select], [data-test=chip-input]')?.focus())
  }
  return { filters, active, clear }
}
