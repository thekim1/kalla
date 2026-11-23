import { computed } from 'vue'
import type { KeysetPage } from '@/api/types'
import { useLoadable } from '@/stores/loadable'

/**
 * A *Load more* list over a keyset endpoint: `reload()` reads the first page, `more()` the page after the rows shown.
 * The rows and the cursor are one loaded value, so both come from the newest call: an older call that ends later
 * changes neither. `data` is the newest page with every row loaded so far (a page's other fields, such as the deletion
 * guard, come from it); `items` can be set to patch rows in place.
 */
export function useKeysetList<P extends KeysetPage<unknown, unknown>>(fetchPage: (after: NonNullable<P['next']> | undefined) => Promise<P>) {
  const { data, loading, error, loaded, run } = useLoadable<P | null>(null)
  const items = computed<P['items']>({
    get: () => data.value?.items ?? [],
    set: (rows) => {
      if (data.value) data.value = { ...data.value, items: rows }
    },
  })
  const next = computed<P['next'] | null>(() => data.value?.next ?? null)

  function reload(): Promise<P | null> {
    return run(() => fetchPage(undefined))
  }

  /** The page after the rows shown when it is called; nothing when there is none. */
  async function more(): Promise<P | null> {
    const previous = data.value
    if (previous?.next == null) return null
    const after = previous.next as NonNullable<P['next']>
    return run(async () => {
      const page = await fetchPage(after)
      return { ...page, items: [...previous.items, ...page.items] }
    })
  }

  return { data, items, next, loading, error, loaded, reload, more }
}
