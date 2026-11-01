import type { RouteRecordRaw } from 'vue-router'
import { PLATFORM_ADMIN } from '@/router/nav'
import { PAGE } from '@/utils/labels'

/** The generic forms for platform objects (admin design §5.8): platform admins only. */
export const platformRoutes: RouteRecordRaw[] = [
  { path: '/plattform', name: 'platform', component: () => import('./PlatformIndexView.vue'), meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN } },
  {
    path: '/plattform/:resource',
    name: 'platform-resource',
    component: () => import('./PlatformResourceView.vue'),
    props: true,
    meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN },
  },
]
