import { onBeforeUnmount, onMounted } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, type RouteLocationNormalized } from 'vue-router'
import { UNSAVED } from '@/utils/labels'

export interface TabGuard {
  /** The page's tab now. */
  current: () => string
  /** The tab a route opens (its `?tab=`, the first tab when absent or unknown). */
  of: (route: RouteLocationNormalized) => string
  /** Asked before the tab changes by a link (`?tab=`); the page's own tab switch asks itself. */
  confirm: () => boolean
}

/**
 * Leaving with unsaved changes asks first (admin design §5.12, *Saving*): a route change in the app, the same page for
 * another object (a copy's new id), a tab change by a link when `tab` is given, a tab change on the page
 * (`confirmLeave()`, called by the tab switch) and closing or reloading the browser tab (the browser's own prompt).
 */
export function useUnsavedGuard(dirty: () => boolean, tab?: TabGuard) {
  const confirmLeave = (): boolean => !dirty() || window.confirm(UNSAVED.leave)

  onBeforeRouteLeave(() => confirmLeave())
  onBeforeRouteUpdate((to, from) => {
    if (to.path !== from.path) return confirmLeave()
    // The page's own switch has set the tab already, so only a link's change is asked about here.
    if (tab && tab.of(to) !== tab.current()) return tab.confirm()
    return true
  })

  const onBeforeUnload = (e: BeforeUnloadEvent) => {
    if (!dirty()) return
    e.preventDefault()
    e.returnValue = ''
  }
  onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))
  onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))

  return { confirmLeave }
}
