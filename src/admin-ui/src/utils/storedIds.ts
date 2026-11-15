/**
 * A list of ids remembered in this browser (localStorage), as Home's Hide (admin design §5.5.1 item 3). Without storage the
 * list is empty and a write does nothing, so a choice then lasts until the page is left.
 */
export function readStoredIds(key: string): string[] {
  try {
    const value: unknown = JSON.parse(window.localStorage.getItem(key) ?? '[]')
    return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : []
  } catch {
    return []
  }
}

export function writeStoredIds(key: string, ids: string[]): void {
  try {
    if (ids.length) window.localStorage.setItem(key, JSON.stringify(ids))
    else window.localStorage.removeItem(key)
  } catch {
    // Storage may be unavailable; the choice then lasts for this visit.
  }
}
