import { ref } from 'vue'
import { api } from '@/api'
import type { AssistantSummary, CollectionSummary, Department, Page } from '@/api/types'

/** The admin API's largest page (`PageQuery`, admin design §4.1). */
const PAGE_SIZE = 200

/** Every row of a paged list: page after page until the total is read, so a long list is never cut off at one page. */
export async function allPages<T>(read: (page: number, pageSize: number) => Promise<Page<T>>): Promise<T[]> {
  const items: T[] = []
  for (let page = 1; ; page++) {
    const next = await read(page, PAGE_SIZE)
    items.push(...next.items)
    if (next.items.length === 0 || items.length >= next.total) return items
  }
}

/**
 * The reference data of the app drawers and their key form (admin design §5.9, §5.9.1): the assistants, the departments
 * with their teams, the address of "How to connect" and, for the key form, every knowledge base. Read once per drawer and
 * passed down; a failed read leaves its list or the address empty, not the drawer broken.
 */
export function useClientChoices(options: { collections?: boolean } = {}) {
  const assistants = ref<AssistantSummary[]>([])
  const departments = ref<Department[]>([])
  const collections = ref<CollectionSummary[]>([])
  const apiBaseUrl = ref<string | null>(null)
  const loading = ref(false)

  async function load(): Promise<void> {
    loading.value = true
    ;[assistants.value, departments.value, collections.value, apiBaseUrl.value] = await Promise.all([
      allPages((page, pageSize) => api.assistants.list({ page, pageSize })).catch(() => []),
      api.organisation.departments().catch(() => []),
      options.collections ? allPages((page, pageSize) => api.collections.list({ page, pageSize, view: 'all' })).catch(() => []) : [],
      api.clients.connection().then((c) => c.apiBaseUrl).catch(() => null),
    ])
    loading.value = false
  }

  return { assistants, departments, collections, apiBaseUrl, loading, load }
}
