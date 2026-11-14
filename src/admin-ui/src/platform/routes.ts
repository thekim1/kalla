import type { RouteRecordRaw } from 'vue-router'
import { PLATFORM_ADMIN } from '@/router/nav'
import { PAGE } from '@/utils/labels'

/**
 * The platform pages (admin design §5.10), platform admins only, each in the side menu: Apps and keys (§5.9), Models and
 * Settings. The old addresses of the Platform objects page and its generic pages lead to where their content went.
 */
export const platformRoutes: RouteRecordRaw[] = [
  { path: '/plattform/clients', name: 'platform-clients', component: () => import('./ClientsView.vue'), meta: { title: PAGE.appsAndKeys.title, roles: PLATFORM_ADMIN } },
  {
    path: '/plattform/gateway-models', name: 'platform-models', component: () => import('./PlatformResourceView.vue'), props: { resource: 'gateway-models' },
    meta: { title: PAGE.models.title, roles: PLATFORM_ADMIN },
  },
  {
    path: '/plattform/settings/:section?', name: 'platform-settings', component: () => import('./SettingsView.vue'), props: true,
    meta: { title: PAGE.settings.title, roles: PLATFORM_ADMIN },
  },
  { path: '/plattform', redirect: { name: 'platform-settings' } },
  { path: '/plattform/keys', redirect: { name: 'platform-clients', query: { tab: 'keys' } } },
  { path: '/plattform/site-profiles', redirect: { name: 'platform-settings', params: { section: 'site-profiles' } } },
  { path: '/plattform/data-protection', redirect: { name: 'platform-settings', params: { section: 'data-protection' } } },
  { path: '/plattform/organisation', redirect: { name: 'organisation' } },
]
