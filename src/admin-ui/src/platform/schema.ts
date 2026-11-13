import { http } from '@/api/client'
import { toLocalInput } from '@/utils/format'
import { PLATFORM } from '@/utils/labels'

/**
 * The request schemas of the generic forms (admin design §5.8), read from the admin API's OpenAPI document: it is
 * generated from the same DataAnnotations the API validates with, so form and server agree. Controls follow the
 * FormModel table of admin design §6.3. The server stays the only validator.
 */

export type JsonSchema = {
  $ref?: string
  type?: string | string[]
  format?: string
  enum?: unknown[]
  oneOf?: JsonSchema[]
  anyOf?: JsonSchema[]
  items?: JsonSchema
  properties?: Record<string, JsonSchema>
  required?: string[]
  title?: string
  description?: string
  minimum?: number
  maximum?: number
  default?: unknown
}

export type OpenApiDocument = {
  paths: Record<string, Record<string, { requestBody?: { content?: Record<string, { schema?: JsonSchema }> } }>>
  components?: { schemas?: Record<string, JsonSchema> }
}

export type FieldKind = 'switch' | 'select' | 'number' | 'text' | 'datetime' | 'list' | 'json' | 'model'

export interface FieldSpec {
  name: string
  label: string
  help?: string
  kind: FieldKind
  required: boolean
  nullable: boolean
  options?: string[]
  /** A `model` field's kind: its picker offers the gateway mirror's models of this kind (modelFields.ts). */
  modelKind?: 'chat' | 'rerank' | 'embedding'
  /** Set on a member of a JSON field: the field it is edited out of and sent back in. */
  parent?: string
}

let document: Promise<OpenApiDocument> | null = null

export function loadOpenApi(): Promise<OpenApiDocument> {
  document ??= http.get<OpenApiDocument>('/openapi/v1.json').catch((e: unknown) => {
    document = null
    throw e
  })
  return document
}

export function resetOpenApiCache(): void {
  document = null
}

function resolve(doc: OpenApiDocument, schema: JsonSchema | undefined): JsonSchema | undefined {
  let current = schema
  for (let depth = 0; current?.$ref && depth < 10; depth++) {
    current = doc.components?.schemas?.[current.$ref.split('/').pop()!]
  }
  return current
}

/** The JSON request body schema of `method path` (path as in the document, e.g. `/api/clients/{id}`). */
export function requestSchema(doc: OpenApiDocument, method: string, path: string): JsonSchema | undefined {
  const body = doc.paths[path]?.[method.toLowerCase()]?.requestBody?.content?.['application/json']?.schema
  return resolve(doc, body)
}

function types(schema: JsonSchema): string[] {
  return schema.type === undefined ? [] : Array.isArray(schema.type) ? schema.type : [schema.type]
}

/** One property's control. A nullable member is `oneOf [null, X]` or `type: [null, X]`. */
function fieldFor(doc: OpenApiDocument, name: string, raw: JsonSchema, required: boolean): FieldSpec {
  let schema = resolve(doc, raw) ?? {}
  let nullable = types(schema).includes('null')
  const variants = schema.oneOf ?? schema.anyOf
  if (variants) {
    nullable ||= variants.some((v) => types(v).includes('null'))
    schema = { ...resolve(doc, variants.find((v) => !types(v).includes('null'))), description: schema.description ?? undefined }
  }
  const t = types(schema).filter((x) => x !== 'null')
  const base = { name, label: PLATFORM.fields[name] ?? schema.title ?? fieldLabel(name), help: PLATFORM.fieldHints[name] ?? schema.description, required, nullable }
  // A nullable enum may list null among its values: the select's empty option stands for it.
  if (schema.enum) {
    return { ...base, nullable: base.nullable || schema.enum.includes(null), kind: 'select', options: schema.enum.filter((v) => v !== null).map(String) }
  }
  if (t.includes('boolean')) return { ...base, kind: 'switch' }
  // ASP.NET's schemas allow numbers as strings too (`[integer, string]` with a pattern).
  if (t.includes('integer') || t.includes('number')) return { ...base, kind: 'number' }
  if (t.includes('array')) {
    const item = resolve(doc, schema.items) ?? {}
    return { ...base, kind: types(item).includes('string') && !item.properties ? 'list' : 'json' }
  }
  if (t.includes('string')) return { ...base, kind: schema.format === 'date-time' ? 'datetime' : 'text' }
  return { ...base, kind: 'json' }
}

/** The form's fields; `version` is the concurrency token, sent back as read and never shown. */
export function fieldsOf(doc: OpenApiDocument, schema: JsonSchema | undefined): FieldSpec[] {
  const required = new Set(schema?.required ?? [])
  return Object.entries(schema?.properties ?? {})
    .filter(([name]) => name !== 'version' && name !== 'impactToken')
    .map(([name, property]) => fieldFor(doc, name, property, required.has(name)))
}

/** A value as the control edits it. */
export function toInput(field: FieldSpec, value: unknown): string | boolean {
  switch (field.kind) {
    case 'switch':
      return value === true
    case 'list':
      return Array.isArray(value) ? value.join('\n') : ''
    case 'json':
      return value === undefined || value === null ? '' : JSON.stringify(value, null, 2)
    case 'datetime':
      return typeof value === 'string' ? toLocalInput(value) : ''
    default:
      return value === undefined || value === null ? '' : String(value)
  }
}

export class InputError extends Error {}

/** The control's value as the request body member; an empty optional field is null (or left out when not nullable). */
export function fromInput(field: FieldSpec, input: string | boolean): unknown {
  if (field.kind === 'switch') return input === true
  const text = String(input).trim()
  if (text === '') return field.kind === 'list' ? [] : field.nullable ? null : undefined
  switch (field.kind) {
    case 'number': {
      const n = Number(text.replace(',', '.'))
      if (!Number.isFinite(n)) throw new InputError('number')
      return n
    }
    case 'list':
      return text.split('\n').map((l) => l.trim()).filter(Boolean)
    case 'datetime': {
      // The input's local date and time, sent as an ISO instant.
      const date = new Date(text)
      if (Number.isNaN(date.getTime())) throw new InputError('date')
      return date.toISOString()
    }
    case 'json':
      try {
        return JSON.parse(text) as unknown
      } catch {
        throw new InputError('json')
      }
    default:
      return text
  }
}

/** `clientRetentionDays` → "Client retention days"; `portalUrl` → "Portal URL". */
export function fieldLabel(name: string): string {
  const words = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').split(/[\s_-]+/).filter(Boolean)
  const text = words.map((w) => (/^(id|url|api|oidc)$/i.test(w) ? w.toUpperCase() : w.toLowerCase())).join(' ')
  return text.charAt(0).toUpperCase() + text.slice(1)
}
