<script setup lang="ts">
export interface FilterOption {
  value: string
  label: string
}

defineProps<{ label: string; modelValue: string; options: FilterOption[] }>()
defineEmits<{ (e: 'update:modelValue', value: string): void }>()
</script>

<template>
  <label class="flex min-w-0 max-w-full items-center gap-2 text-small text-fg-2">
    <span class="whitespace-nowrap">{{ label }}</span>
    <select :value="modelValue" class="field-input !h-ctl w-auto min-w-0 max-w-full truncate sm:min-w-[120px]" @change="$emit('update:modelValue', ($event.target as HTMLSelectElement).value)">
      <option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option>
    </select>
  </label>
</template>
