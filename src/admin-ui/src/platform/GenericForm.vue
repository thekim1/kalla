<script setup lang="ts">
import { reactive, ref } from 'vue'
import { isApiError, request } from '@/api/client'
import type { GatewayModel, Level } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, LEVEL_LABEL, PLATFORM } from '@/utils/labels'
import { PLATFORM_RULE_MESSAGES } from '@/utils/messages'
import { isObject, memberOf, modelHint, modelOptions } from './modelFields'
import { fromInput, InputError, toInput, type FieldSpec } from './schema'

// One request of a platform object as a form (admin design §5.8): the fields of its request schema, prefilled from
// `initial`; `version` goes back as read. The server validates; its field errors land on the fields. A `model` field picks
// from `models` (the gateway mirror, #466); one with a `parent` is a member of that JSON field, edited above it and
// merged back into it on save. The `extra` slot gets `collect()`, the body as it would be sent (null when a field cannot be
// read), for an action on unsaved values.
const props = withDefaults(
  defineProps<{
    formId: string
    fields: FieldSpec[]
    initial: Record<string, unknown>
    method: 'post' | 'put'
    url: string
    submitLabel: string
    models?: GatewayModel[]
    /** Fields shown but not editable (an embedding profile in use, #565); they are still sent back as loaded. */
    readOnly?: readonly string[]
  }>(),
  { models: () => [], readOnly: () => [] },
)
const emit = defineEmits<{ (e: 'saved', response: unknown): void; (e: 'cancel'): void }>()

// Mounted per form (the parent keys it), so fields and the start values are read once.
const idOf = (name: string) => `${props.formId}-${name}`
const membersOf = (parent: string) => props.fields.filter((f) => f.parent === parent).map((f) => f.name)
function startValue(field: FieldSpec): unknown {
  if (field.parent) return memberOf(props.initial[field.parent], field.name)
  const value = props.initial[field.name]
  const members = membersOf(field.name)
  if (members.length === 0 || !isObject(value)) return value
  return Object.fromEntries(Object.entries(value).filter(([k]) => !members.includes(k)))
}
const values = reactive<Record<string, string | boolean>>(Object.fromEntries(props.fields.map((f) => [f.name, toInput(f, startValue(f))])))
const saving = ref(false)
const form = useFormErrors(Object.fromEntries(props.fields.map((f) => [f.name, idOf(f.name)])))

function hint(field: FieldSpec): string | undefined {
  const extra = field.kind === 'list' ? PLATFORM.form.listHint : field.kind === 'json' ? PLATFORM.form.jsonHint : undefined
  const picked = field.kind === 'json' && membersOf(field.name).length > 0 ? PLATFORM.form.membersAbove : undefined
  return [field.help, extra, picked].filter(Boolean).join(' ') || undefined
}

/** A select option's text: an information class by its name in the UI (admin design §5.7), anything else as it is. */
function optionLabel(value: string): string {
  return LEVEL_LABEL[value as Level] ?? value
}

/** A platform rule's own text when the refusal names a constraint that has one (`limits_from_gateway`, admin design §4.10). */
function platformRuleMessage(error: unknown): string | undefined {
  if (!isApiError(error) || error.problem?.code !== 'platform_rule') return undefined
  const constraint = error.problem.params?.constraint
  return constraint ? PLATFORM_RULE_MESSAGES[constraint] : undefined
}

/** The request body from the controls, or null after marking the fields that cannot be read. */
function collect(): Record<string, unknown> | null {
  const body: Record<string, unknown> = {}
  const problems: Record<string, string> = {}
  const members: Record<string, Record<string, unknown>> = {}
  for (const field of props.fields) {
    try {
      const value = fromInput(field, values[field.name] ?? '')
      // An unset member is left out: the section's reader treats a missing member as unset.
      if (field.parent) {
        if (value !== undefined && value !== null && !(Array.isArray(value) && value.length === 0)) (members[field.parent] ??= {})[field.name] = value
      } else if (value !== undefined) body[field.name] = value
      else if (field.required) problems[field.name] = PLATFORM.form.required
    } catch (e) {
      if (!(e instanceof InputError)) throw e
      problems[field.name] = { json: PLATFORM.form.invalidJson, date: PLATFORM.form.invalidDate }[e.message] ?? PLATFORM.form.invalidNumber
    }
  }
  for (const parent of new Set(props.fields.flatMap((f) => (f.parent ? [f.parent] : [])))) {
    const rest = body[parent] ?? {}
    if (isObject(rest)) body[parent] = { ...rest, ...members[parent] }
    else problems[parent] ??= PLATFORM.form.invalidJson
  }
  if (!form.validate(problems)) return null
  if (props.initial.version !== undefined && props.initial.version !== null) body.version = props.initial.version
  return body
}

async function save(): Promise<void> {
  const body = collect()
  if (!body) return
  saving.value = true
  try {
    emit('saved', await request(props.method.toUpperCase() as 'POST' | 'PUT', props.url, { body }))
  } catch (e) {
    form.applyServerError(e)
    const rule = platformRuleMessage(e)
    if (rule) form.formError.value = rule
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form :id="formId" class="grid gap-5" novalidate @submit.prevent="save">
    <template v-for="field in fields" :key="field.name">
      <div v-if="field.kind === 'switch'" class="grid gap-1">
        <!-- A visible label that also names the switch (#565; WCAG 2.5.3 Label in Name, 3.3.2 Labels or Instructions). -->
        <div class="flex items-center gap-3">
          <UiSwitch
            v-model="values[field.name] as boolean" :labelledby="`${idOf(field.name)}-label`" :describedby="field.help ? `${idOf(field.name)}-help` : undefined"
            :disabled="readOnly.includes(field.name)"
          />
          <span :id="`${idOf(field.name)}-label`" class="font-medium">{{ field.label }}</span>
        </div>
        <p v-if="field.help" :id="`${idOf(field.name)}-help`" class="text-small text-fg-3">{{ field.help }}</p>
      </div>
      <SelectField
        v-else-if="field.kind === 'select'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label" :disabled="readOnly.includes(field.name)"
        :options="[...(field.required ? [] : [{ value: '', label: PLATFORM.form.none }]), ...(field.options ?? []).map((o) => ({ value: o, label: optionLabel(o) }))]"
        :hint="field.help" :error="form.errors[field.name]"
      />
      <SelectField
        v-else-if="field.kind === 'model'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label" :disabled="readOnly.includes(field.name)"
        :options="modelOptions(field, models, values[field.name] as string)" :hint="modelHint(field, models)" :error="form.errors[field.name]"
      />
      <!-- A JSON field whose members have fields above holds only the rest: under Advanced, open when it is refused. -->
      <details v-else-if="field.kind === 'json' && membersOf(field.name).length > 0" :open="!!form.errors[field.name]" data-test="advanced-json">
        <summary class="cursor-pointer font-medium">{{ PLATFORM.form.advancedJson }}</summary>
        <p class="mt-1 text-small text-fg-3">{{ PLATFORM.form.advancedJsonHint }}</p>
        <TextAreaField
          :id="idOf(field.name)" v-model="values[field.name] as string" class="mt-3" :label="field.label" :hint="hint(field)" :error="form.errors[field.name]"
          :rows="8" mono
        />
      </details>
      <TextAreaField
        v-else-if="field.kind === 'json' || field.kind === 'list'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label"
        :hint="hint(field)" :error="form.errors[field.name]" :rows="field.kind === 'json' ? 8 : 3" mono
      />
      <TextField
        v-else-if="field.kind === 'datetime'" :id="idOf(field.name)" v-model="values[field.name] as string" type="datetime-local" :label="field.label" :hint="field.help"
        :disabled="readOnly.includes(field.name)" :error="form.errors[field.name]"
      />
      <TextField
        v-else :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label" :hint="field.help" :disabled="readOnly.includes(field.name)"
        :error="form.errors[field.name]" :inputmode="field.kind === 'number' ? 'decimal' : undefined" autocomplete="off"
      />
    </template>
    <!-- Extra controls that act on the values as entered (an embedding profile's Test), before the form's own error. -->
    <slot name="extra" :collect="collect" />
    <InlineError v-if="form.formError.value" :message="form.formError.value" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" :loading="saving" data-test="save">{{ submitLabel }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </form>
</template>
