/** `fetchMode` of a URL-list or crawl source (contracts/sources/v1, connectors design §8.1). */
export type FetchMode = 'http' | 'browser'

/** The form's JavaScript-page fields (FetchModeFields.vue). */
export interface FetchForm {
  fetchMode: FetchMode
  readySelector: string
  scrollToLoad: boolean
}

/** The schema's `readySelector` maxLength. */
export const READY_SELECTOR_MAX = 500

/** The fields as a saved source's settings hold them (the schema's defaults where absent). */
export function fetchForm(settings: Record<string, unknown>): FetchForm {
  return {
    fetchMode: settings.fetchMode === 'browser' ? 'browser' : 'http',
    readySelector: typeof settings.readySelector === 'string' ? settings.readySelector : '',
    scrollToLoad: settings.scrollToLoad === true,
  }
}

/**
 * `settings` with the form's fetch settings: the browser-only ones only with a browser, and a default left out, so an
 * HTTP source's settings hold none of them.
 */
export function withFetchSettings(settings: Record<string, unknown>, form: FetchForm): Record<string, unknown> {
  const rest = { ...settings }
  delete rest.fetchMode
  delete rest.readySelector
  delete rest.scrollToLoad
  if (form.fetchMode === 'http') return rest
  const selector = form.readySelector.trim()
  return { ...rest, fetchMode: 'browser', ...(selector ? { readySelector: selector } : {}), ...(form.scrollToLoad ? { scrollToLoad: true } : {}) }
}
