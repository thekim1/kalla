import { computed, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

/**
 * A page's filters kept in the address (admin design §5.12 *Filters*): `?period=30d&rating=down`, so a filtered view can
 * be linked, survives a reload and comes back with back and forward. Each key has a default; a key at its default is
 * left out of the address. A change replaces the query (no history entry, no reload, focus stays where it is), and a
 * change of the address (a link, back, forward) is followed.
 */
export function useQueryFilters<K extends string>(defaults: Record<K, string>) {
  const route = useRoute()
  const router = useRouter()
  const keys = Object.keys(defaults) as K[]
  const read = (): Record<K, string> =>
    Object.fromEntries(keys.map((k) => [k, typeof route?.query[k] === 'string' ? (route.query[k] as string) : defaults[k]])) as Record<K, string>
  const filters = reactive(read()) as Record<K, string>

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
  /** Clear filters: every key back to its default. */
  function clear(): void {
    for (const k of keys) filters[k] = defaults[k]
  }
  return { filters, active, clear }
}
