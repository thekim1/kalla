<script setup lang="ts">
import { reactive, ref } from 'vue'
import { request } from '@/api/client'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, PLATFORM } from '@/utils/labels'
import { fromInput, InputError, toInput, type FieldSpec } from './schema'

// One request of a platform object as a form (admin design §5.8): the fields of its request schema, prefilled from
// `initial`; `version` goes back as read. The server validates; its field errors land on the fields.
const props = defineProps<{ formId: string; fields: FieldSpec[]; initial: Record<string, unknown>; method: 'post' | 'put'; url: string; submitLabel: string }>()
const emit = defineEmits<{ (e: 'saved', response: unknown): void; (e: 'cancel'): void }>()

// Mounted per form (the parent keys it), so fields and the start values are read once.
const idOf = (name: string) => `${props.formId}-${name}`
const values = reactive<Record<string, string | boolean>>(Object.fromEntries(props.fields.map((f) => [f.name, toInput(f, props.initial[f.name])])))
const saving = ref(false)
const form = useFormErrors(Object.fromEntries(props.fields.map((f) => [f.name, idOf(f.name)])))

function hint(field: FieldSpec): string | undefined {
  const extra = field.kind === 'list' ? PLATFORM.form.listHint : field.kind === 'json' ? PLATFORM.form.jsonHint : undefined
  return [field.help, extra].filter(Boolean).join(' ') || undefined
}

async function save(): Promise<void> {
  const body: Record<string, unknown> = {}
  const problems: Record<string, string> = {}
  for (const field of props.fields) {
    try {
      const value = fromInput(field, values[field.name] ?? '')
      if (value !== undefined) body[field.name] = value
      else if (field.required) problems[field.name] = PLATFORM.form.required
    } catch (e) {
      if (!(e instanceof InputError)) throw e
      problems[field.name] = e.message === 'json' ? PLATFORM.form.invalidJson : PLATFORM.form.invalidNumber
    }
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
