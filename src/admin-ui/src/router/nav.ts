import type { RouteLocationRaw } from 'vue-router'
import { ROLES, type Role } from '@/api/types'
import { platformNavItems } from '@/platform/nav'
import { rolesFor } from '@/utils/permissions'
import { NAV_GROUP, NAV_KEYWORDS, PAGE } from '@/utils/labels'

/** A place inside a page (a tab or a card) that search can jump straight to. */
export interface NavSection {
  label: string
  to: RouteLocationRaw
  keywords?: string[]
}

export interface NavItem {
  name: string
  label: string
  icon: string
  /** One line on what the page is for; shown in search and as the menu tooltip. */
  description?: string
  /** Extra words people search with that are not in the label. */
  keywords?: string[]
  sections?: NavSection[]
  /** Other route names that belong to this page, so it stays highlighted on them. */
  children?: string[]
  roles?: Role[]
  /** Attention items, as the gateway's badges (admin design §5.2): Home's Needs-attention count (B-59). */
  badge?: 'attention'
}

export interface NavGroup {
  id: string
  label: string
  items: NavItem[]
}

/** Every rag-* role: the Read policy (admin design §3.1). */
export const ALL_ROLES: Role[] = [...ROLES]
/** The Admin policy (admin design §3.1), from the permission matrix. */
export const PLATFORM_ADMIN: Role[] = rolesFor('admin')
/** The Manage policy (admin design §3.1), from the permission matrix. */
export const MANAGE_ROLES: Role[] = rolesFor('manage')
/**
 * The Feedback policy (admin design §3.3), from the permission matrix: platform and department admins, reviewers and
 * viewers; not the editor roles.
 */
export const FEEDBACK_ROLES: Role[] = rolesFor('feedback')

/**
 * Grouped by the job people come to do (admin design §5.2), filtered by role: Home and Map; Knowledge bases and
 * Assistants; Feedback; then the platform pages (Apps and keys, Models, Settings), Organisation and the Audit log (§5.10).
 */
export const navGroups: NavGroup[] = [
  {
    id: 'overview',
    label: NAV_GROUP.overview,
    items: [
      {
        name: 'overview',
        label: PAGE.overview.title,
        icon: 'space_dashboard',
        description: PAGE.overview.description,
        keywords: [...NAV_KEYWORDS.overview],
        roles: ALL_ROLES,
        badge: 'attention',
      },
      {
        name: 'map',
        label: PAGE.map.title,
        icon: 'hub',
        description: PAGE.map.description,
        keywords: [...NAV_KEYWORDS.map],
        roles: ALL_ROLES,
      },
    ],
  },
  {
    id: 'content',
    label: NAV_GROUP.content,
    items: [
      {
        name: 'collections',
        label: PAGE.collections.title,
        icon: 'library_books',
        description: PAGE.collections.description,
        keywords: [...NAV_KEYWORDS.collections],
        roles: ALL_ROLES,
        // A knowledge base's pages keep the menu item lit (admin design §5.12, Every page).
        children: ['collection', 'source', 'run', 'publication'],
      },
      {
        name: 'assistants',
        label: PAGE.assistants.title,
        icon: 'smart_toy',
        description: PAGE.assistants.description,
        keywords: [...NAV_KEYWORDS.assistants],
        roles: ALL_ROLES,
        children: ['assistant'],
      },
    ],
  },
  {
    id: 'follow-up',
    label: NAV_GROUP.followUp,
    items: [
      {
        name: 'feedback',
        label: PAGE.feedback.title,
        icon: 'thumbs_up_down',
        description: PAGE.feedback.description,
        keywords: [...NAV_KEYWORDS.feedback],
        roles: FEEDBACK_ROLES,
      },
      {
        name: 'costs',
        label: PAGE.costs.title,
        icon: 'payments',
        description: PAGE.costs.description,
        keywords: [...NAV_KEYWORDS.costs],
        roles: MANAGE_ROLES,
      },
    ],
  },
  {
    id: 'platform',
    label: NAV_GROUP.platform,
    items: [
      ...platformNavItems,
      {
        name: 'organisation',
        label: PAGE.organisation.title,
        icon: 'account_tree',
        description: PAGE.organisation.description,
        keywords: [...NAV_KEYWORDS.organisation],
        roles: ALL_ROLES,
      },
      {
        name: 'audit',
        label: PAGE.audit.title,
        icon: 'history',
        description: PAGE.audit.description,
        keywords: [...NAV_KEYWORDS.audit],
        roles: MANAGE_ROLES,
      },
    ],
  },
]

/** Bottom bar on phones (admin design §5.2); the rest is under Menu. */
export const mobileTabs: NavItem[] = [
  { name: 'overview', label: PAGE.overview.title, icon: 'space_dashboard', roles: ALL_ROLES, badge: 'attention' },
  { name: 'collections', label: PAGE.collections.title, icon: 'library_books', roles: ALL_ROLES },
]

/** True when the current route is the item's page or one of its sub-pages. */
export function isNavItemActive(item: NavItem, routeName: unknown): boolean {
  return routeName === item.name || (typeof routeName === 'string' && !!item.children?.includes(routeName))
}

export interface SearchEntry {
  id: string
  label: string
  /** The page a section belongs to; empty for the page itself. */
  parent?: string
  group: string
  icon: string
  description?: string
  to: RouteLocationRaw
  text: string
}

/** Flat list of pages and their sections for search, in menu order. */
export function searchEntries(groups: NavGroup[]): SearchEntry[] {
  return groups.flatMap((g) =>
    g.items.flatMap((item) => {
      const page: SearchEntry = {
        id: item.name,
        label: item.label,
        group: g.label,
        icon: item.icon,
        description: item.description,
        to: { name: item.name },
        text: [item.label, item.description, g.label, ...(item.keywords ?? [])].join(' ').toLowerCase(),
      }
      const sections = (item.sections ?? []).map<SearchEntry>((s, i) => ({
        id: `${item.name}:${i}`,
        label: s.label,
        parent: item.label,
        group: g.label,
        icon: item.icon,
        to: s.to,
        text: [s.label, item.label, ...(s.keywords ?? [])].join(' ').toLowerCase(),
      }))
      return [page, ...sections]
    }),
  )
}

/** Every word of the query must appear; label matches rank first. */
export function filterEntries(entries: SearchEntry[], query: string): SearchEntry[] {
  const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (!words.length) return entries.filter((e) => !e.parent)
  const matches = entries.filter((e) => words.every((w) => e.text.includes(w)))
  const first = words[0] ?? ''
  const rank = (e: SearchEntry) => (e.label.toLowerCase().startsWith(first) ? 0 : e.label.toLowerCase().includes(first) ? 1 : 2)
  return matches.map((e, i) => ({ e, i, r: rank(e) })).sort((a, b) => a.r - b.r || a.i - b.i).map((x) => x.e)
}
