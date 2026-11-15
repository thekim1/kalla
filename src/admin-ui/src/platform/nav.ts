import type { NavItem } from '@/router/nav'
import { NAV_KEYWORDS, PAGE, PLATFORM } from '@/utils/labels'
import { SETTINGS_PAGE_SECTIONS } from './settingsPage'

/** The platform pages in the side menu's Platform group (admin design §5.10), before Organisation and the Audit log. */
export const platformNavItems: NavItem[] = [
  {
    name: 'platform-clients',
    label: PAGE.appsAndKeys.title,
    icon: 'apps',
    description: PAGE.appsAndKeys.description,
    keywords: [...(PLATFORM.resources.clients.keywords ?? [])],
    roles: ['rag-platform-admin'],
  },
  {
    name: 'platform-models',
    label: PAGE.models.title,
    icon: 'hub',
    description: PAGE.models.description,
    keywords: [...NAV_KEYWORDS.models],
    roles: ['rag-platform-admin'],
  },
  {
    name: 'platform-settings',
    label: PAGE.settings.title,
    icon: 'tune',
    description: PAGE.settings.description,
    keywords: [...NAV_KEYWORDS.settings],
    // Each section is found by its name.
    sections: SETTINGS_PAGE_SECTIONS.map((s) => ({ label: s.label, to: { name: 'platform-settings', params: { section: s.id } } })),
    roles: ['rag-platform-admin'],
  },
]
