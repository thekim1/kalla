import { describe, expect, it } from 'vitest'
import { fieldsOf, fromInput, requestSchema, toInput } from './schema'

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
})
