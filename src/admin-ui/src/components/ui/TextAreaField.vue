<script setup lang="ts">
import FormField from './FormField.vue'
import { NO_PASSWORD_MANAGER } from './noAutofill'

/** Multi-line text field; `mono` for code such as JSON. */
defineProps<{ label: string; modelValue: string; id?: string; hint?: string; error?: string | null; placeholder?: string; rows?: number; mono?: boolean }>()
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <FormField :id="id" v-slot="{ id: fieldId, describedBy, invalid }" :label="label" :hint="hint" :error="error">
    <textarea
      :id="fieldId"
      :name="fieldId"
      :value="modelValue"
      :rows="rows ?? 3"
      :placeholder="placeholder"
      autocomplete="off"
      v-bind="NO_PASSWORD_MANAGER"
      :spellcheck="mono ? 'false' : undefined"
      :aria-invalid="invalid ? 'true' : undefined"
      :aria-describedby="describedBy"
      class="field-input !h-auto min-h-[88px] resize-y py-2"
      :class="mono ? 'font-mono text-small' : ''"
      @input="$emit('update:modelValue', ($event.target as HTMLTextAreaElement).value)"
    />
  </FormField>
</template>
