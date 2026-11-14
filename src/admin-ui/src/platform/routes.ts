import type { RouteRecordRaw } from 'vue-router'
import { PLATFORM_ADMIN } from '@/router/nav'
import { PAGE } from '@/utils/labels'

/** The platform objects (admin design §5.8): platform admins only. Apps and keys (§5.9) and Settings (§5.10) have pages of their own, before the generic route. */
export const platformRoutes: RouteRecordRaw[] = [
  { path: '/plattform', name: 'platform', component: () => import('./PlatformIndexView.vue'), meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN } },
  // The generic Keys page went (admin design §5.9.1): its address opens All keys.
  { path: '/plattform/keys', redirect: { name: 'platform-clients', query: { tab: 'keys' } } },
  // Settings (admin design §5.10): a page of sections; site profiles and data protection are sections of it.
  {
    path: '/plattform/settings/:section?', name: 'platform-settings', component: () => import('./SettingsView.vue'), props: true,
    meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN },
  },
  { path: '/plattform/site-profiles', redirect: { name: 'platform-settings', params: { section: 'site-profiles' } } },
  { path: '/plattform/data-protection', redirect: { name: 'platform-settings', params: { section: 'data-protection' } } },
  { path: '/plattform/clients', name: 'platform-clients', component: () => import('./ClientsView.vue'), meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN } },
  {
    path: '/plattform/:resource',
    name: 'platform-resource',
    component: () => import('./PlatformResourceView.vue'),
    props: true,
    meta: { title: PAGE.platform.title, roles: PLATFORM_ADMIN },
  },
]
