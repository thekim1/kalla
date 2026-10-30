import { nextTick } from 'vue'
import { createRouter, createWebHistory, type RouteRecordRaw, type RouterHistory } from 'vue-router'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { APP, PAGE } from '@/utils/labels'
import { ALL_ROLES } from './nav'

declare module 'vue-router' {
  interface RouteMeta {
    /** Page title used for document.title. */
    title?: string
    /** Roles allowed to open the page. Empty/undefined = any signed-in user. */
    roles?: Role[]
  }
}

export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'overview', component: () => import('@/views/OverviewView.vue'), meta: { title: PAGE.overview.title, roles: ALL_ROLES } },
  { path: '/organisation', name: 'organisation', component: () => import('@/views/OrganisationView.vue'), meta: { title: PAGE.organisation.title, roles: ALL_ROLES } },
  { path: '/forbidden', name: 'forbidden', component: () => import('@/views/ForbiddenView.vue'), meta: { title: PAGE.forbidden.title } },
  { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('@/views/NotFoundView.vue'), meta: { title: PAGE.notFound.title } },
]

/** Moves focus to the page heading after client-side navigation (not on first load). */
export function focusPageHeading(): void {
  void nextTick(() => {
    window.setTimeout(() => {
      const heading = document.getElementById('page-title')
      if (heading) heading.focus({ preventScroll: false })
      else document.getElementById('main')?.focus()
    }, 0)
  })
}

/**
 * Moves focus to a section heading linked with #id (search jumps to cards this way). Data loads
 * after navigation, so the heading may appear a little later; falls back to the page heading.
 */
export function focusSection(hash: string): void {
  const id = decodeURIComponent(hash.slice(1))
  let tries = 0
  const attempt = () => {
    const target = document.getElementById(id)
    if (target) {
      if (!target.hasAttribute('tabindex')) target.setAttribute('tabindex', '-1')
      target.classList.add('outline-none')
      target.scrollIntoView({ block: 'start' })
      target.focus({ preventScroll: true })
    } else if (++tries < 20) window.setTimeout(attempt, 100)
    else focusPageHeading()
  }
  void nextTick(() => window.setTimeout(attempt, 0))
}

export function createAppRouter(history: RouterHistory = createWebHistory()) {
  const router = createRouter({
    history,
    routes,
    scrollBehavior(to, _from, saved) {
      // Hash targets are scrolled by focusSection once the section has rendered.
      if (to.hash) return false
      return saved ?? { top: 0 }
    },
  })

  router.beforeEach(async (to) => {
    const auth = useAuthStore()
    await auth.ensureLoaded()
    // Signed-out users see the sign-in page rendered by App.vue regardless of route.
    if (!auth.isAuthenticated) return true
    if (!auth.canOpen(to.meta)) {
      return { name: 'forbidden', query: { from: to.fullPath }, replace: true }
    }
    return true
  })

  let firstNavigation = true
  router.afterEach((to, from, failure) => {
    if (failure) return
    document.title = `${to.meta.title ?? APP.name} · ${APP.titleSuffix}`
    if (firstNavigation) {
      firstNavigation = false
      if (to.hash) focusSection(to.hash)
      return
    }
    if (to.hash) focusSection(to.hash)
    else if (to.path !== from.path) focusPageHeading()
  })

  return router
}
