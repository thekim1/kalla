import { describe, expect, it } from 'vitest'
import type { GatewayModel } from '@/api/types'
import { MODEL_AVAILABILITY, PLATFORM } from '@/utils/labels'
import { modelHint, modelOptions, withModelFields } from './modelFields'
import type { FieldSpec } from './schema'

const model = (name: string, modelKind: GatewayModel['modelKind'], available: boolean | null): GatewayModel =>
  ({ name, kind: 'model', modelKind, residencies: ['onprem'], available })

const chat: FieldSpec = { name: 'condenseModel', label: 'Condense model', kind: 'model', modelKind: 'chat', required: false, nullable: true }
const rerank: FieldSpec = { ...chat, name: 'gatewayRerankModel', modelKind: 'rerank' }

describe('model fields (#466)', () => {
  it('turns a property into a picker and puts a JSON member\'s picker just above its JSON field', () => {
    const fields: FieldSpec[] = [
      { name: 'evaluationModel', label: 'x', kind: 'text', required: false, nullable: true },
      { name: 'value', label: 'value', kind: 'json', required: true, nullable: false },
    ]
    const result = withModelFields(fields, [{ name: 'evaluationModel', modelKind: 'chat' }, { name: 'gatewayRerankModel', modelKind: 'rerank', in: 'value' }])
    expect(result.map((f) => [f.name, f.kind, f.modelKind, f.parent])).toEqual([
      ['evaluationModel', 'model', 'chat', undefined],
      ['gatewayRerankModel', 'model', 'rerank', 'value'],
      ['value', 'json', undefined, undefined],
    ])
    expect(result[1]!.label).toBe(PLATFORM.fields.gatewayRerankModel)
  })

  it('offers unset and the models of the field\'s kind; one the key may not use is disabled and marked', () => {
    const models = [model('gpt-oss', 'chat', true), model('claude', 'chat', false), model('rerank-1', 'rerank', true), model('embed', 'embedding', true)]
    expect(modelOptions(chat, models, '')).toEqual([
      { value: '', label: PLATFORM.form.none },
      { value: 'gpt-oss', label: 'gpt-oss', disabled: false },
      { value: 'claude', label: `claude — ${MODEL_AVAILABILITY.notAvailable}`, disabled: true },
    ])
    expect(modelOptions(rerank, models, '').map((o) => o.value)).toEqual(['', 'rerank-1'])
    expect(modelOptions({ ...chat, required: true }, models, 'gpt-oss')[0]!.value).toBe('gpt-oss')
  })

  it('keeps a current value that is no model of the kind any more, marked, and never disables the current one', () => {
    const models = [model('gpt-oss', 'chat', true), model('claude', 'chat', false)]
    expect(modelOptions(chat, models, 'retired')).toContainEqual({ value: 'retired', label: `retired — ${MODEL_AVAILABILITY.notListed}` })
    expect(modelOptions(chat, models, 'claude')).toContainEqual({ value: 'claude', label: `claude — ${MODEL_AVAILABILITY.notAvailable}`, disabled: false })
  })

  it('adds "availability unknown" to the hint only when no model of the kind has a known availability', () => {
    expect(modelHint({ ...chat, help: 'Help.' }, [model('a', 'chat', null), model('b', 'chat', null), model('r', 'rerank', true)])).toBe(`Help. ${MODEL_AVAILABILITY.unknownOnSave}`)
    expect(modelHint(rerank, [model('a', 'chat', null), model('r', 'rerank', true)])).toBeUndefined()
    expect(modelHint(chat, [model('a', 'chat', null), model('b', 'chat', true)])).toBeUndefined()
    expect(modelHint(chat, [])).toBeUndefined()
  })
})
