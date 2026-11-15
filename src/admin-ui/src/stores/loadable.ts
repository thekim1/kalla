import { ref, type Ref } from 'vue'

/** Small helper for thin resource stores: tracks loading/error around an API call. */
export function useLoadable<T>(initial: T) {
  const data = ref(initial) as Ref<T>
  const loading = ref(false)
  const error = ref<unknown>(null)
  const loaded = ref(false)
  let latest = 0

  /** Only the newest call's answer or error is kept; an older call that ends later returns null and changes nothing. */
  async function run(fetcher: () => Promise<T>): Promise<T | null> {
    const mine = ++latest
    loading.value = true
    error.value = null
    try {
      const result = await fetcher()
      if (mine !== latest) return null
      data.value = result
      loaded.value = true
      return result
    } catch (e) {
      if (mine === latest) error.value = e
      return null
    } finally {
      if (mine === latest) loading.value = false
    }
  }

  return { data, loading, error, loaded, run }
}

export function replaceById<T extends { id: string }>(list: T[], item: T): T[] {
  const index = list.findIndex((x) => x.id === item.id)
  return index === -1 ? [...list, item] : list.map((x) => (x.id === item.id ? item : x))
}

export function removeById<T extends { id: string }>(list: T[], id: string): T[] {
  return list.filter((x) => x.id !== id)
}
