import type { NavItem } from '@/router/nav'
import { PAGE } from '@/utils/labels'
import { platformEntries } from './resources'

export const platformNavItems: NavItem[] = [
  {
    name: 'platform',
    label: PAGE.platform.title,
    icon: 'settings',
    description: PAGE.platform.description,
    keywords: ['clients', 'keys', 'settings', 'data protection', 'site profiles', 'models', 'templates', 'member groups', 'sharing'],
    sections: platformEntries.map((e) => ({ label: e.label, to: e.to, keywords: e.keywords ? [...e.keywords] : undefined })),
    children: ['platform-clients', 'platform-resource'],
    roles: ['rag-platform-admin'],
  },
]
