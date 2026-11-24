import { onBeforeUnmount, onMounted } from 'vue'
import { onBeforeRouteLeave } from 'vue-router'
import { UNSAVED } from '@/utils/labels'

/**
 * Leaving with unsaved changes asks first (admin design §5.12, *Saving*): a route change in the app, a tab change on the
 * page (`confirmLeave()`, called by the tab switch) and closing or reloading the browser tab (the browser's own prompt).
 */
export function useUnsavedGuard(dirty: () => boolean) {
  const confirmLeave = (): boolean => !dirty() || window.confirm(UNSAVED.leave)

  onBeforeRouteLeave(() => confirmLeave())

  const onBeforeUnload = (e: BeforeUnloadEvent) => {
    if (!dirty()) return
    e.preventDefault()
    e.returnValue = ''
  }
  onMounted(() => window.addEventListener('beforeunload', onBeforeUnload))
  onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))

  return { confirmLeave }
}
