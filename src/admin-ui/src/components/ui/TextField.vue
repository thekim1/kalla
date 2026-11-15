<script setup lang="ts">
import FormField from './FormField.vue'
import { NO_PASSWORD_MANAGER } from './noAutofill'

const props = withDefaults(
  defineProps<{
    label: string
    modelValue: string
    id?: string
    type?: string
    hint?: string
    error?: string | null
    placeholder?: string
    inputmode?: 'text' | 'numeric' | 'decimal' | 'url' | 'email'
    mono?: boolean
    disabled?: boolean
    autocomplete?: string
    name?: string
    min?: string | number
    max?: string | number
    /** A short input (a number of a few digits) instead of the full width; the label and hint keep the width. */
    short?: boolean
  }>(),
  { type: 'text' },
)
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <FormField :id="props.id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :hint="hint" :error="error">
    <input
      :id="fieldId"
      :name="name ?? fieldId"
      :value="modelValue"
      :type="type"
      :inputmode="inputmode"
      :placeholder="placeholder"
      :disabled="disabled"
      :autocomplete="autocomplete ?? 'off'"
      v-bind="autocomplete ? {} : NO_PASSWORD_MANAGER"
      :min="min"
      :max="max"
      :aria-invalid="invalid ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="field-input"
      :class="{ 'font-mono': mono, 'max-w-48': short }"
      @input="$emit('update:modelValue', ($event.target as HTMLInputElement).value)"
    />
  </FormField>
</template>
