import type { GatewayModel } from '@/api/types'
import type { SelectOption } from '@/components/ui/SelectField.vue'
import { MODEL_AVAILABILITY, PLATFORM } from '@/utils/labels'
import { fieldLabel, type FieldSpec } from './schema'

/**
 * Model settings as pickers over the gateway mirror (Decisions "Model pickers show what the key may use", #447/#466), and
 * other members of a JSON property edited as fields of their own (admin design §5.10 step 2). A field is a request property
 * (`evaluationModel`) or a member of a JSON property (`condenseModel` in `value`).
 */
export interface ModelField {
  name: string
  /** The models the picker offers; absent for a plain `number` member (`kind`). */
  modelKind?: 'chat' | 'rerank' | 'embedding'
  /**
   * Not a model: `number` (the chat section's answer ceiling, B-31), `list` (one value per line) or `text`. Empty leaves the
   * member out, so the reader's default applies; the hint says what that default is. Default: a model picker.
   */
  kind?: 'number' | 'list' | 'text'
  /** The JSON property the member lives in; absent for a property of its own. */
  in?: string
}

/** The form's fields with each model field as a `model` control; a JSON member's picker goes just above its JSON field. */
export function withModelFields(fields: FieldSpec[], models: readonly ModelField[]): FieldSpec[] {
  const result = fields.map((f) => {
    const own = models.find((m) => !m.in && m.name === f.name)
    return own ? { ...f, kind: own.kind ?? ('model' as const), modelKind: own.kind ? undefined : own.modelKind } : f
  })
  for (const m of models.filter((x) => x.in)) {
    const at = result.findIndex((f) => f.name === m.in && f.kind === 'json')
    if (at < 0) continue
    result.splice(at, 0, {
      name: m.name,
      label: PLATFORM.fields[m.name] ?? fieldLabel(m.name),
      help: PLATFORM.fieldHints[m.name],
      kind: m.kind ?? 'model',
      modelKind: m.kind ? undefined : m.modelKind,
      required: false,
      nullable: true,
      parent: m.in,
    })
  }
  return result
}

/**
 * The picker's options: unset (unless required), then the mirror's models of the field's kind. One the platform's key
 * may not use (`available: false`) is disabled and marked; `null` keeps it choosable. A current value that is not among
 * them stays as a marked option, so saving the form does not drop it; the current value is never disabled.
 */
export function modelOptions(field: FieldSpec, models: readonly GatewayModel[], current: string): SelectOption[] {
  const ofKind = models.filter((m) => m.modelKind === field.modelKind)
  const options: SelectOption[] = field.required ? [] : [{ value: '', label: PLATFORM.form.none }]
  if (current && !ofKind.some((m) => m.name === current)) options.push({ value: current, label: `${current} — ${MODEL_AVAILABILITY.notListed}` })
  for (const m of ofKind) {
    const unavailable = m.available === false
    options.push({ value: m.name, label: unavailable ? `${m.name} — ${MODEL_AVAILABILITY.notAvailable}` : m.name, disabled: unavailable && m.name !== current })
  }
  return options
}

/** The field's hint, with "availability unknown" when the gateway could not be asked for any model of its kind. */
export function modelHint(field: FieldSpec, models: readonly GatewayModel[]): string | undefined {
  const ofKind = models.filter((m) => m.modelKind === field.modelKind)
  const unknown = ofKind.length > 0 && ofKind.every((m) => m.available == null)
  return [field.help, unknown ? MODEL_AVAILABILITY.unknownOnSave : undefined].filter(Boolean).join(' ') || undefined
}

export function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/** A JSON object's member; undefined for anything else. */
export function memberOf(value: unknown, name: string): unknown {
  return isObject(value) ? value[name] : undefined
}
