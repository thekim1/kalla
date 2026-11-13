import type { RouteRecordRaw } from 'vue-router'
import { PLATFORM_ADMIN } from '@/router/nav'
import { PAGE } from '@/utils/labels'

/** The platform objects (admin design §5.8): platform admins only. Clients have a page of their own (§5.9), before the generic route. */
export const platformRoutes: RouteRecordRaw[] = [
  { path: '/plattform', name: 'platform', component: () => import('./PlatformIndexView.vue'), meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN } },
  // The generic Keys page went (admin design §5.9.1): its address opens All keys.
  { path: '/plattform/keys', redirect: { name: 'platform-clients', query: { tab: 'keys' } } },
  { path: '/plattform/clients', name: 'platform-clients', component: () => import('./ClientsView.vue'), meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN } },
  {
    path: '/plattform/:resource',
    name: 'platform-resource',
    component: () => import('./PlatformResourceView.vue'),
    props: true,
    meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN },
  },
]
