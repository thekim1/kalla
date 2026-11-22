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

/** A row's non-CRUD action: one whose request has fields opens the form, any other is sent at once. */
export interface RowAction extends Operation {
  id: string
  label: string
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
  /**
   * The row's DELETE (`{param}` replaced by the row's key, its `version` sent as the query's `version`); the drawer of an
   * existing row then has a Delete button that asks first, adding `deleteNote`.
   */
  remove?: { path: string; deleteNote?: string }
  /** A heading above the list, for a page whose panels come first. */
  listTitle?: string
  /** Panels shown above the list, in order. */
  panels?: readonly ('gateway-context' | 'embedding-profiles' | 'embedding-canary')[]
  /** The row's edit fields that pick a gateway model (#466); the form then loads the mirror with its availability. */
  modelFields?: (row: Row) => readonly ModelField[] | undefined
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
 * `Admin:GatewayAdminUrl`; null when neither is set or the status cannot be read. Links are made with `gatewayHref`, which
 * takes only an http(s) address.
 */
export async function gatewayAdminUrl(): Promise<string | null> {
  return (await orgSyncStatus())?.adminUrl ?? null
}

export async function listRows(resource: PlatformResource): Promise<Row[]> {
  const payload = await http.get<Row[] | { items: Row[] }>(resource.list!)
  return Array.isArray(payload) ? payload : payload.items
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
    // Assistant templates (admin design §4.5, B-54): a section of the Settings page, under Chat and answers.
    id: 'assistant-templates',
    ...L.assistantTemplates,
    icon: 'library_books',
    list: '/api/assistant-templates',
    key: 'id',
    columns: [
      { key: 'name', label: PLATFORM.columns.name },
      { key: 'description', label: PLATFORM.fields.description! },
      { key: 'isActive', label: PLATFORM.columns.enabled },
    ],
    create: { method: 'post', path: '/api/assistant-templates' },
    update: { method: 'put', path: '/api/assistant-templates/{id}' },
    remove: { path: '/api/assistant-templates/{id}', deleteNote: L.assistantTemplates.deleteNote },
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
    listTitle: PLATFORM.gatewayModels.listTitle,
    panels: ['gateway-context', 'embedding-profiles', 'embedding-canary'],
  },
]

export async function rowsOf(resource: PlatformResource): Promise<Row[]> {
  return listRows(resource)
}

export function resourceById(id: string): PlatformResource | undefined {
  return platformResources.find((r) => r.id === id)
}
