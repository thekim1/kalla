<script setup lang="ts">
import { reactive, ref } from 'vue'
import { request } from '@/api/client'
import type { GatewayModel } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, PLATFORM } from '@/utils/labels'
import { isObject, memberOf, modelHint, modelOptions } from './modelFields'
import { fromInput, InputError, toInput, type FieldSpec } from './schema'

// One request of a platform object as a form (admin design §5.8): the fields of its request schema, prefilled from
// `initial`; `version` goes back as read. The server validates; its field errors land on the fields. A `model` field picks
// from `models` (the gateway mirror, #466); one with a `parent` is a member of that JSON field, edited above it and
// merged back into it on save.
const props = withDefaults(
  defineProps<{ formId: string; fields: FieldSpec[]; initial: Record<string, unknown>; method: 'post' | 'put'; url: string; submitLabel: string; models?: GatewayModel[] }>(),
  { models: () => [] },
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

async function save(): Promise<void> {
  const body: Record<string, unknown> = {}
  const problems: Record<string, string> = {}
  const members: Record<string, Record<string, unknown>> = {}
  for (const field of props.fields) {
    try {
      const value = fromInput(field, values[field.name] ?? '')
      // An unset member is left out: the section's reader treats a missing member as unset.
      if (field.parent) {
        if (value !== undefined && value !== null) (members[field.parent] ??= {})[field.name] = value
      } else if (value !== undefined) body[field.name] = value
      else if (field.required) problems[field.name] = PLATFORM.form.required
    } catch (e) {
      if (!(e instanceof InputError)) throw e
      problems[field.name] = e.message === 'json' ? PLATFORM.form.invalidJson : PLATFORM.form.invalidNumber
    }
  }
  for (const parent of new Set(props.fields.flatMap((f) => (f.parent ? [f.parent] : [])))) {
    const rest = body[parent] ?? {}
    if (isObject(rest)) body[parent] = { ...rest, ...members[parent] }
    else problems[parent] ??= PLATFORM.form.invalidJson
  }
  if (!form.validate(problems)) return
  if (props.initial.version !== undefined && props.initial.version !== null) body.version = props.initial.version
  saving.value = true
  try {
    emit('saved', await request(props.method.toUpperCase() as 'POST' | 'PUT', props.url, { body }))
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form :id="formId" class="grid gap-5" novalidate @submit.prevent="save">
    <template v-for="field in fields" :key="field.name">
      <div v-if="field.kind === 'switch'" class="grid gap-1">
        <UiSwitch v-model="values[field.name] as boolean" :label="field.label" />
        <p v-if="field.help" class="text-small text-fg-3">{{ field.help }}</p>
      </div>
      <SelectField
        v-else-if="field.kind === 'select'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label"
        :options="[...(field.required ? [] : [{ value: '', label: PLATFORM.form.none }]), ...(field.options ?? []).map((o) => ({ value: o, label: o }))]"
        :hint="field.help" :error="form.errors[field.name]"
      />
      <SelectField
        v-else-if="field.kind === 'model'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label"
        :options="modelOptions(field, models, values[field.name] as string)" :hint="modelHint(field, models)" :error="form.errors[field.name]"
      />
      <TextAreaField
        v-else-if="field.kind === 'json' || field.kind === 'list'" :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label"
        :hint="hint(field)" :error="form.errors[field.name]" :rows="field.kind === 'json' ? 8 : 3" mono
      />
      <TextField
        v-else :id="idOf(field.name)" v-model="values[field.name] as string" :label="field.label" :hint="field.help"
        :error="form.errors[field.name]" :inputmode="field.kind === 'number' ? 'decimal' : undefined" autocomplete="off"
      />
    </template>
    <InlineError v-if="form.formError.value" :message="form.formError.value" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" :loading="saving" data-test="save">{{ submitLabel }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </form>
</template>
