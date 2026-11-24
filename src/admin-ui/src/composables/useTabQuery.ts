import { ref, watch, type Ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'

/**
 * A page's tab kept in `?tab=`, so a reload or a shared link opens the same tab: read when the page opens, written
 * when the tab changes (a replace, so tabs add no history entries) and followed when the query changes (a link to
 * another tab of the same page). The first tab is the default and leaves no `tab` in the query; an unknown value
 * opens it.
 */
export function useTabQuery<T extends string>(tabs: readonly [T, ...T[]], aliases: Record<string, T> = {}): Ref<T> {
  const route = useRoute()
  const router = useRouter()
  // A renamed tab's old value (a bookmark, an old link) opens the tab it became.
  const parse = (value: unknown): T => (tabs.includes(value as T) ? (value as T) : (aliases[String(value)] ?? tabs[0]))
  const tab = ref(parse(route?.query.tab)) as Ref<T>

  watch(tab, (t) => {
    if (!route || !router?.replace || parse(route.query.tab) === t) return
    const query = { ...route.query, tab: t === tabs[0] ? undefined : t }
    if (query.tab === undefined) delete query.tab
    void router.replace({ query })
  })
  watch(() => route?.query.tab, (value) => {
    tab.value = parse(value)
  })
  return tab
}
