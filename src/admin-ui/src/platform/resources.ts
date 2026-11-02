import { http } from '@/api/client'
import { PLATFORM } from '@/utils/labels'

/**
 * The platform objects with a generic form (admin design §5.8). Each entry names its endpoints as written in the
 * OpenAPI document; the form's fields come from that operation's request schema (schema.ts).
 */

export type Row = Record<string, unknown>

export interface Operation {
  method: 'post' | 'put'
  /** The path as in the OpenAPI document; `{param}` is replaced by the row's key. */
  path: string
}

export interface RowAction extends Operation {
  id: string
  label: string
  /** Asked before the call; actions whose request has fields open the form instead. */
  confirm?: string
}

export interface ResourceColumn {
  key: string
  label: string
  /** The cell's value when it is not `row[key]`. */
  value?: (row: Row) => unknown
}

export interface Details {
  title: string
  items: { label: string; value: string }[]
}

export interface PlatformResource {
  id: string
  label: string
  description: string
  /** Extra words search finds the resource by (its section in the platform page's search entry). */
  keywords?: readonly string[]
  icon: string
  /** The GET that lists the rows (an array or a page `{ items }`). */
  list?: string
  key: string
  columns: ResourceColumn[]
  /** Read-only facts about the row, shown above its form. */
  details?: (row: Row) => Details
  create?: Operation
  update?: Operation
  /** The values an edit starts from; the row itself when absent. */
  load?: (row: Row) => Promise<Row>
  actions?: RowAction[]
}

export function urlOf(path: string, row: Row | null, key: string): string {
  return row ? path.replace(/\{[^}]+\}/, encodeURIComponent(String(row[key]))) : path
}

export async function listRows(resource: PlatformResource): Promise<Row[]> {
  const payload = await http.get<Row[] | { items: Row[] }>(resource.list!)
  return Array.isArray(payload) ? payload : payload.items
}

export interface SharingSummary {
  department: number
  selected: number
  municipality: number
  sharedWithDepartments: number
}

function sharingOf(row: Row): SharingSummary {
  return (row.sharing as SharingSummary | undefined) ?? { department: 0, selected: 0, municipality: 0, sharedWithDepartments: 0 }
}

const SETTINGS_SECTIONS = ['network', 'retention', 'deletion-guard', 'crawl', 'evaluation', 'retrieval', 'chat', 'pii', 'worker'] as const

const L = PLATFORM.resources

export const platformResources: PlatformResource[] = [
  {
    id: 'clients',
    ...L.clients,
    icon: 'apps',
    list: '/api/clients',
    key: 'id',
    columns: [
      { key: 'name', label: PLATFORM.columns.name },
      { key: 'kind', label: PLATFORM.columns.kind },
      { key: 'classificationCeiling', label: PLATFORM.columns.ceiling },
      { key: 'isEnabled', label: PLATFORM.columns.enabled },
    ],
    create: { method: 'post', path: '/api/clients' },
    update: { method: 'put', path: '/api/clients/{id}' },
    load: (row) => http.get<Row>(urlOf('/api/clients/{id}', row, 'id')),
  },
  {
    id: 'keys',
    ...L.keys,
    icon: 'key',
    list: '/api/keys',
    key: 'id',
    columns: [
      { key: 'name', label: PLATFORM.columns.name },
      { key: 'prefix', label: PLATFORM.columns.prefix },
      { key: 'status', label: PLATFORM.columns.status },
      { key: 'expiresAt', label: PLATFORM.columns.expiresAt },
    ],
    create: { method: 'post', path: '/api/keys' },
    update: { method: 'put', path: '/api/keys/{id}' },
    // The key's scopes are flat on the row; the update request takes them as one object.
    load: async (row) => ({ ...row, scopes: { assistantIds: row.assistantIds ?? [], collectionIds: row.collectionIds ?? [] } }),
    actions: [
      { id: 'rotate', label: PLATFORM.actions.rotate, method: 'post', path: '/api/keys/{id}/rotate' },
      { id: 'reveal', label: PLATFORM.actions.reveal, method: 'post', path: '/api/keys/{id}/reveal' },
      { id: 'revoke', label: PLATFORM.actions.revoke, method: 'post', path: '/api/keys/{id}/revoke', confirm: PLATFORM.actions.revokeConfirm },
    ],
  },
  {
    id: 'settings',
    ...L.settings,
    icon: 'tune',
    key: 'section',
    columns: [{ key: 'label', label: PLATFORM.columns.section }],
    update: { method: 'put', path: '/api/settings/{section}' },
    load: (row) => http.get<Row>(`/api/settings/${String(row.section)}`),
  },
  {
    id: 'data-protection',
    ...L.dataProtection,
    icon: 'shield_lock',
    list: '/api/data-protection',
    key: 'classification',
    columns: [
      { key: 'classification', label: PLATFORM.columns.level },
      { key: 'defaultHistoryMode', label: PLATFORM.columns.historyMode },
      { key: 'retentionDefaultDays', label: PLATFORM.columns.retentionDays },
      { key: 'encryptionRequired', label: PLATFORM.columns.encryption },
    ],
    update: { method: 'put', path: '/api/data-protection/{level}' },
  },
  {
    id: 'site-profiles',
    ...L.siteProfiles,
    icon: 'web',
    list: '/api/site-profiles',
    key: 'id',
    columns: [
      { key: 'host', label: PLATFORM.columns.host },
      { key: 'presetId', label: PLATFORM.columns.preset },
      { key: 'appliedVersion', label: PLATFORM.columns.version },
      { key: 'appliedAt', label: PLATFORM.columns.appliedAt },
    ],
    create: { method: 'post', path: '/api/site-profiles' },
    update: { method: 'put', path: '/api/site-profiles/{id}' },
    // An edit is a new version on top of the applied one (admin design §4.5): the version's profile, the number as base.
    load: async (row) => {
      const applied = await http.get<Row>(`/api/site-profiles/${String(row.id)}/versions/${String(row.appliedVersion)}`)
      return { presetId: row.presetId, profile: applied.profile, version: row.appliedVersion }
    },
    actions: [{ id: 'rollback', label: PLATFORM.actions.rollback, method: 'post', path: '/api/site-profiles/{id}/rollback' }],
  },
  {
    id: 'gateway-models',
    ...L.gatewayModels,
    icon: 'hub',
    list: '/api/gateway/models',
    key: 'name',
    columns: [
      { key: 'name', label: PLATFORM.columns.name },
      { key: 'kind', label: PLATFORM.columns.kind },
      { key: 'residencies', label: PLATFORM.columns.residencies },
      { key: 'source', label: PLATFORM.columns.source },
    ],
  },
  {
    id: 'organisation',
    ...L.organisation,
    icon: 'account_tree',
    list: '/api/organisation/departments',
    key: 'id',
    columns: [
      { key: 'name', label: PLATFORM.columns.name },
      { key: 'costCenterCode', label: PLATFORM.columns.costCenter },
      { key: 'memberGroups', label: PLATFORM.columns.memberGroups },
      {
        key: 'sharing',
        label: PLATFORM.columns.sharing,
        value: (row) => {
          const s = sharingOf(row)
          return PLATFORM.organisation.summary(s.department, s.selected, s.municipality)
        },
      },
    ],
    details: (row) => {
      const s = sharingOf(row)
      const O = PLATFORM.organisation
      return {
        title: O.sharing,
        items: [
          { label: O.department, value: String(s.department) },
          { label: O.selected, value: String(s.selected) },
          { label: O.sharedWith, value: String(s.sharedWithDepartments) },
          { label: O.municipality, value: String(s.municipality) },
        ],
      }
    },
    update: { method: 'put', path: '/api/organisation/departments/{id}/member-groups' },
  },
]

/** Settings have no list endpoint: one row per section of `config."Settings"` (admin design §4.8). */
export async function rowsOf(resource: PlatformResource): Promise<Row[]> {
  if (resource.id === 'settings') {
    return SETTINGS_SECTIONS.map((section) => ({ section, label: PLATFORM.settingsSections[section] }))
  }
  return listRows(resource)
}

export function resourceById(id: string): PlatformResource | undefined {
  return platformResources.find((r) => r.id === id)
}
