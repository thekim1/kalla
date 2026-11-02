import type { NavItem } from '@/router/nav'
import { PAGE } from '@/utils/labels'
import { platformResources } from './resources'

export const platformNavItems: NavItem[] = [
  {
    name: 'platform',
    label: PAGE.platform.title,
    icon: 'settings',
    description: PAGE.platform.description,
    keywords: ['clients', 'keys', 'settings', 'data protection', 'site profiles', 'models', 'templates'],
    sections: platformResources.map((r) => ({ label: r.label, to: { name: 'platform-resource', params: { resource: r.id } } })),
    children: ['platform-resource'],
    roles: ['rag-platform-admin'],
  },
]
