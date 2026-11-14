import type { NavItem } from '@/router/nav'
import { PAGE } from '@/utils/labels'
import { platformEntries } from './resources'
import { SETTINGS_PAGE_SECTIONS } from './settingsPage'

export const platformNavItems: NavItem[] = [
  {
    name: 'platform',
    label: PAGE.platform.title,
    icon: 'settings',
    description: PAGE.platform.description,
    keywords: ['clients', 'keys', 'settings', 'data protection', 'site profiles', 'models', 'templates', 'member groups', 'sharing'],
    sections: [
      ...platformEntries.map((e) => ({ label: e.label, to: e.to, keywords: e.keywords ? [...e.keywords] : undefined })),
      // Each settings section is found by its name (admin design §5.10).
      ...SETTINGS_PAGE_SECTIONS.map((s) => ({ label: s.label, to: { name: 'platform-settings', params: { section: s.id } }, keywords: ['settings'] })),
    ],
    children: ['platform-clients', 'platform-settings', 'platform-resource'],
    roles: ['rag-platform-admin'],
  },
]
