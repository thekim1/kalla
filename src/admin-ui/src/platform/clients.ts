import type { Client, ClientKind, ClientRequest, Level } from '@/api/types'
import type { Tone } from '@/utils/labels'

/**
 * The Clients page's rules (admin design §5.9): which kinds can be made now, which fields each kind shows, and the
 * defaults a new client of that kind starts from.
 */

/** In the order of the kind step; the rest show "Later" (P2). */
export const AVAILABLE_KINDS: readonly ClientKind[] = ['open-webui', 'integration', 'internal']
export const LATER_KINDS: readonly ClientKind[] = ['widget', 'own-client', 'teams', 'mcp-client', 'entra-app']

export type ClientField = 'name' | 'classificationCeiling' | 'storesHistory' | 'clientRetentionDays' | 'oAuthClientId' | 'portalUrl' | 'isEnabled'

const FIELDS_BY_KIND: Record<string, readonly ClientField[]> = {
  'open-webui': ['name', 'classificationCeiling', 'storesHistory', 'clientRetentionDays', 'portalUrl', 'isEnabled'],
  integration: ['name', 'classificationCeiling', 'storesHistory', 'oAuthClientId', 'isEnabled'],
  internal: ['name', 'classificationCeiling', 'oAuthClientId', 'isEnabled'],
}

/** A kind's fields in form order; a kind made elsewhere (P2) shows the fields every kind has. */
export function fieldsFor(kind: string): readonly ClientField[] {
  return FIELDS_BY_KIND[kind] ?? ['name', 'classificationCeiling', 'isEnabled']
}

export const LEVELS: readonly Level[] = ['public', 'internal', 'sensitive']

/** The controls' values: text fields as typed, so a number field can be checked on save. */
export interface ClientForm {
  name: string
  classificationCeiling: Level
  storesHistory: boolean
  clientRetentionDays: string
  oAuthClientId: string
  portalUrl: string
  isEnabled: boolean
}

const CEILING_BY_KIND: Record<string, Level> = { 'open-webui': 'internal', internal: 'sensitive', integration: 'public' }

export function newClientForm(kind: string): ClientForm {
  return {
    name: '',
    classificationCeiling: CEILING_BY_KIND[kind] ?? 'internal',
    storesHistory: kind === 'open-webui',
    clientRetentionDays: kind === 'open-webui' ? '90' : '',
    oAuthClientId: '',
    portalUrl: '',
    isEnabled: true,
  }
}

export function clientForm(client: Client): ClientForm {
  return {
    name: client.name,
    classificationCeiling: client.classificationCeiling,
    storesHistory: client.storesHistory,
    clientRetentionDays: client.clientRetentionDays === null ? '' : String(client.clientRetentionDays),
    oAuthClientId: client.oAuthClientId ?? '',
    portalUrl: client.portalUrl ?? '',
    isEnabled: client.isEnabled,
  }
}

/** `StoresHistory ⇒ ceiling ≠ sensitive` (the Clients table's CHECK). */
export function historyAllowed(ceiling: Level): boolean {
  return ceiling !== 'sensitive'
}

export class RetentionDaysError extends Error {}

/**
 * The request from the form. A field the kind does not show is sent as `base` has it (the loaded client on an edit)
 * or empty on a create; `toolPolicy` and `allowedOrigins` are not shown (P2), so an edit sends them back as read and a
 * create leaves `toolPolicy` to the server's default.
 */
export function clientRequest(kind: string, form: ClientForm, base: Client | null): ClientRequest {
  const shown = new Set(fieldsFor(kind))
  const text = (value: string) => value.trim() || null
  let days: number | null = base?.clientRetentionDays ?? null
  if (shown.has('clientRetentionDays')) {
    const value = form.clientRetentionDays.trim()
    days = value === '' ? null : Number(value)
    if (days !== null && !Number.isInteger(days)) throw new RetentionDaysError()
  }
  return {
    name: form.name.trim(),
    kind,
    classificationCeiling: form.classificationCeiling,
    storesHistory: shown.has('storesHistory') ? form.storesHistory && historyAllowed(form.classificationCeiling) : (base?.storesHistory ?? false),
    clientRetentionDays: days,
    oAuthClientId: shown.has('oAuthClientId') ? text(form.oAuthClientId) : (base?.oAuthClientId ?? null),
    portalUrl: shown.has('portalUrl') ? text(form.portalUrl) : (base?.portalUrl ?? null),
    isEnabled: form.isEnabled,
    allowedOrigins: base?.allowedOrigins ?? [],
    ...(base ? { toolPolicy: base.toolPolicy, version: base.version } : {}),
  }
}

/** The two Open WebUI lines of the deployment's stack env (deploy/portainer/stack.env.example), copied together. */
export function stackLines(secret: string, clientId: string): string {
  return `UME_RAG_OPENWEBUI_API_KEY=${secret}\nUME_RAG_OPENWEBUI_CLIENT_ID=${clientId}`
}

const LEVEL_RANK: Record<Level, number> = { public: 0, internal: 1, sensitive: 2 }

/**
 * V7: an app may offer an assistant whose level is at most its own information class. (V9, no `sensitive` assistant for an
 * app that keeps history, follows from it: such an app cannot be `sensitive`.)
 */
export function assistantAllowed(assistantLevel: Level, ceiling: Level): boolean {
  return LEVEL_RANK[assistantLevel] <= LEVEL_RANK[ceiling]
}

/** The OpenAI-compatible root an app calls (`GET /v1/models`, `POST /v1/chat/completions`). */
export function apiAddress(apiBaseUrl: string): string {
  return `${apiBaseUrl.replace(/\/+$/, '')}/v1`
}

/** One chat call to try the connection with; `<...>` stands for a value the page cannot show. */
export function curlExample(apiBaseUrl: string | null, secret: string | null, model: string | null): string {
  const address = apiBaseUrl ? apiAddress(apiBaseUrl) : 'https://<rag-api address>/v1'
  const body = JSON.stringify({ model: model ?? '<model name>', messages: [{ role: 'user', content: 'Hello' }] })
  return [
    `curl ${address}/chat/completions \\`,
    `  -H "Authorization: Bearer ${secret ?? '<key>'}" \\`,
    '  -H "Content-Type: application/json" \\',
    `  -d '${body}'`,
  ].join('\n')
}

/** A key status's badge tone, in the app's drawer and on All keys. */
export const KEY_STATUS_TONE: Record<string, Tone> = { active: 'ok', 'in-grace-period': 'warn', expired: 'neutral', revoked: 'danger', disabled: 'neutral' }
