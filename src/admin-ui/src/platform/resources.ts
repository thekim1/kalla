import type { RouteLocationRaw } from 'vue-router'
import { http } from '@/api/client'
import type { GatewayContextInfo, OrgSyncStatus } from '@/api/types'
import { formatNumber } from '@/utils/format'
import { PLATFORM } from '@/utils/labels'
import type { ModelField } from './modelFields'

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

/** A read-only section of the row's drawer: facts as label and value, or a plain list. */
export interface Details {
  title: string
  items?: { label: string; value: string }[]
  lines?: string[]
  /** A sentence under the facts. */
  note?: string
}

/** A page of the gateway's admin where the rows are edited (admin design §2.3). */
export interface GatewayPage {
  /** Appended to the gateway's `admin_url`. */
  path: string
  label: string
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
  /** Read-only facts about the row, shown above its form (or alone, for a row without an edit). */
  details?: (row: Row) => Details[]
  /** Linked in the page header when the gateway's admin URL is known. */
  gatewayPage?: GatewayPage
  create?: Operation
  update?: Operation
  /** Whether a row takes `update`; every row when absent. A row that does not opens its details alone. */
  editable?: (row: Row) => boolean
  /** The values an edit starts from; the row itself when absent. */
  load?: (row: Row) => Promise<Row>
  actions?: RowAction[]
  /** Panels shown above the list, in order. */
  panels?: readonly ('gateway-context' | 'embedding-profiles' | 'embedding-canary')[]
  /** The row's edit fields that pick a gateway model (#466); the form then loads the mirror with its availability. */
  modelFields?: (row: Row) => readonly ModelField[] | undefined
}

/** An entry of the Platform objects page and its search sections: a generic resource or a page of its own. */
export interface PlatformEntry {
  id: string
  label: string
  description: string
  keywords?: readonly string[]
  icon: string
  to: RouteLocationRaw
}

export function urlOf(path: string, row: Row | null, key: string): string {
  return row ? path.replace(/\{[^}]+\}/, encodeURIComponent(String(row[key]))) : path
}

/** The organisation sync's status (`GET /api/organisation/sync`); null when it cannot be read. */
export async function orgSyncStatus(): Promise<OrgSyncStatus | null> {
  try {
    return await http.get<OrgSyncStatus>('/api/organisation/sync')
  } catch {
    return null
  }
}

/**
 * The gateway's last context as the organisation sync holds it in memory, without a gateway call (the same value
 * `GET /api/gateway/status` returns as `gateway`, admin design §4.10); null before the first read or when the status
 * cannot be read.
 */
export async function gatewayContext(): Promise<GatewayContextInfo | null> {
  return (await orgSyncStatus())?.gateway ?? null
}

/**
 * The gateway links' address (admin design §2.3): the status's `adminUrl`, which is the context's `admin_url`, else
 * `Admin:GatewayAdminUrl`; null when neither is set, the status cannot be read, or the address is not an http(s) URL
 * (it becomes a link's `href`).
 */
export async function gatewayAdminUrl(): Promise<string | null> {
  const url = (await orgSyncStatus())?.adminUrl
  return url && /^https?:\/\//i.test(url) ? url : null
}

export function gatewayLink(adminUrl: string, page: GatewayPage): string {
  return `${adminUrl.replace(/\/+$/, '')}${page.path}`
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

const L = PLATFORM.resources

export const platformResources: PlatformResource[] = [
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
      { key: 'contextWindow', label: PLATFORM.fields.contextWindow! },
      { key: 'maxOutputTokens', label: PLATFORM.fields.maxOutputTokens! },
      { key: 'limitsSource', label: PLATFORM.gatewayModels.limitsSource, value: (row) => PLATFORM.gatewayModels.limitsSources[String(row.limitsSource)] },
    ],
    // A row's limits (Decisions "Answer length from the model", admin design §4.10): set here until the gateway's catalogue
    // sends them; from then on they are read-only, and the API refuses a write (platform_rule limits_from_gateway).
    update: { method: 'put', path: '/api/gateway/models/{name}' },
    editable: (row) => row.limitsSource !== 'catalog',
    details: (row) =>
      row.limitsSource === 'catalog'
        ? [
            {
              title: PLATFORM.gatewayModels.limits,
              items: [
                { label: PLATFORM.fields.contextWindow!, value: formatNumber(row.contextWindow as number | null) },
                { label: PLATFORM.fields.maxOutputTokens!, value: formatNumber(row.maxOutputTokens as number | null) },
              ],
              note: PLATFORM.gatewayModels.limitsFromGateway,
            },
          ]
        : [],
    panels: ['gateway-context', 'embedding-profiles', 'embedding-canary'],
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
    // Member groups come from the gateway's context (identity §2.1) and are edited on its Organisation page.
    details: (row) => {
      const s = sharingOf(row)
      const O = PLATFORM.organisation
      const groups = (row.memberGroups as string[] | undefined) ?? []
      return [
        { title: O.memberGroups, lines: groups.length ? groups : [O.noMemberGroups] },
        {
          title: O.sharing,
          items: [
            { label: O.department, value: String(s.department) },
            { label: O.selected, value: String(s.selected) },
            { label: O.sharedWith, value: String(s.sharedWithDepartments) },
            { label: O.municipality, value: String(s.municipality) },
          ],
        },
      ]
    },
    gatewayPage: { path: '/organisation', label: PLATFORM.organisation.gatewayLink },
  },
]

/**
 * The Platform objects page's entries and their search sections: Apps and keys and Settings are pages of their own
 * (admin design §5.9, §5.10); site profiles and data protection are sections of Settings, so they are not listed here.
 */
const IN_SETTINGS = new Set(['site-profiles', 'data-protection'])

export const platformEntries: PlatformEntry[] = [
  { id: 'clients', ...L.clients, icon: 'apps', to: { name: 'platform-clients' } },
  { id: 'settings', ...L.settings, icon: 'tune', to: { name: 'platform-settings' } },
  ...platformResources.filter((r) => !IN_SETTINGS.has(r.id)).map((r) => ({
    id: r.id, label: r.label, description: r.description, keywords: r.keywords, icon: r.icon, to: { name: 'platform-resource', params: { resource: r.id } },
  })),
]

export async function rowsOf(resource: PlatformResource): Promise<Row[]> {
  return listRows(resource)
}

export function resourceById(id: string): PlatformResource | undefined {
  return platformResources.find((r) => r.id === id)
}
