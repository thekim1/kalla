import { describe, expect, it } from 'vitest'
import { fieldLabel, fieldsOf, fromInput, requestSchema, toInput } from './schema'

// A cut of the admin API's OpenAPI document with the shapes ASP.NET generates.
const doc = {
  paths: { '/api/clients/{id}': { put: { requestBody: { content: { 'application/json': { schema: { $ref: '#/components/schemas/ClientUpdateRequest' } } } } } } },
  components: {
    schemas: {
      ClientUpdateRequest: {
        required: ['name', 'classificationCeiling'],
        properties: {
          name: { type: 'string', maxLength: 200 },
          classificationCeiling: { enum: ['public', 'internal', 'sensitive'] },
          storesHistory: { type: 'boolean' },
          clientRetentionDays: { type: ['null', 'integer', 'string'], pattern: '^-?(?:0|[1-9]\\d*)$' },
          allowedOrigins: { type: 'array', items: { type: 'string' } },
          scopes: { oneOf: [{ type: 'null' }, { $ref: '#/components/schemas/Scopes' }], description: 'Null keeps the scopes.' },
          version: { type: ['null', 'string'] },
        },
      },
      Scopes: { type: 'object', properties: { collectionIds: { type: 'array', items: { type: 'string' } } } },
    },
  },
}

describe('generic form schema (admin design §5.8, §6.3)', () => {
  it('maps each property to its control and leaves out the version', () => {
    const fields = fieldsOf(doc, requestSchema(doc, 'PUT', '/api/clients/{id}'))
    expect(fields.map((f) => [f.name, f.kind, f.required, f.nullable])).toEqual([
      ['name', 'text', true, false],
      ['classificationCeiling', 'select', true, false],
      ['storesHistory', 'switch', false, false],
      ['clientRetentionDays', 'number', false, true],
      ['allowedOrigins', 'list', false, false],
      ['scopes', 'json', false, true],
    ])
    expect(fields[1]!.options).toEqual(['public', 'internal', 'sensitive'])
    expect(fields[5]!.help).toBe('Null keeps the scopes.')
  })

  it('round-trips values through the controls', () => {
    const [name, , history, days, origins, scopes] = fieldsOf(doc, requestSchema(doc, 'put', '/api/clients/{id}'))
    expect(fromInput(origins!, toInput(origins!, ['https://a', 'https://b']))).toEqual(['https://a', 'https://b'])
    expect(fromInput(scopes!, toInput(scopes!, { collectionIds: ['c1'] }))).toEqual({ collectionIds: ['c1'] })
    expect(fromInput(history!, toInput(history!, true))).toBe(true)
    expect(fromInput(days!, '30')).toBe(30)
    expect(fromInput(days!, '')).toBeNull()
    expect(fromInput(name!, '')).toBeUndefined()
    expect(() => fromInput(scopes!, '{nope')).toThrow('json')
  })

  it('makes a nullable enum a select without a null option, and a date-time a date and time input that round-trips as ISO (§5.8)', () => {
    const schema = {
      properties: {
        mode: { oneOf: [{ type: 'null' }, { $ref: '#/components/schemas/Mode' }] },
        policy: { type: ['null', 'string'], enum: ['none', 'read', null] },
        expiresAt: { type: ['null', 'string'], format: 'date-time' },
      },
    }
    const withMode = { ...doc, components: { schemas: { ...doc.components.schemas, Mode: { type: 'string', enum: ['RevokeImmediately', 'Grace24Hours'] } } } }
    const [mode, policy, expiresAt] = fieldsOf(withMode, schema)
    expect([mode!.kind, mode!.nullable, mode!.options]).toEqual(['select', true, ['RevokeImmediately', 'Grace24Hours']])
    expect([policy!.kind, policy!.nullable, policy!.options]).toEqual(['select', true, ['none', 'read']])
    expect(fromInput(policy!, '')).toBeNull()

    expect(expiresAt!.kind).toBe('datetime')
    const input = toInput(expiresAt!, '2026-12-31T22:59:59Z')
    expect(input).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}$/)
    expect(fromInput(expiresAt!, input)).toBe('2026-12-31T22:59:59.000Z')
    expect(fromInput(expiresAt!, '')).toBeNull()
    expect(toInput(expiresAt!, null)).toBe('')
    expect(() => fromInput(expiresAt!, 'tomorrow')).toThrow('date')
  })
})

describe('fieldLabel', () => {
  it('names a field from its property name when no label is set', () => {
    expect(fieldLabel('clientRetentionDays')).toBe('Client retention days')
    expect(fieldLabel('portalUrl')).toBe('Portal URL')
    expect(fieldLabel('name')).toBe('Name')
  })
})
