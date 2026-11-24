<script setup lang="ts">
import { computed, useId } from 'vue'
import { FILTERS } from '@/utils/labels'

export interface ChipOption {
  value: string
  label: string
}

/**
 * One filter of a filter row (admin design §5.12 *Filters*): a chip whose name says the filter and its value
 * ("Rating: Thumbs down"), a native select inside it, so the keyboard and screen readers get a real list box. A set chip
 * is coloured and has an × of its own ("Remove the rating filter"); unset, it shows the `any` text. Built in Källa until
 * Fors takes it (§5.12, Shared components).
 */
const props = defineProps<{ label: string; modelValue: string; options: ChipOption[]; any: string; unset?: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string): void }>()
const id = `chip-${useId()}`
/** The value that means "not filtered" (empty by default). */
const unsetValue = computed(() => props.unset ?? '')
const set = computed(() => props.modelValue !== unsetValue.value)
</script>

<template>
  <span
    class="inline-flex h-ctl items-center rounded-full text-small max-md:h-11"
    :class="set ? 'bg-accent-soft text-accent-ink shadow-inset-selected' : 'bg-surface text-fg shadow-inset-input'"
    :data-test="`chip-${label}`"
  >
    <label :for="id" class="cursor-pointer pl-3 font-medium">{{ label }}:</label>
    <select
      :id="id" :value="modelValue" class="h-full cursor-pointer appearance-none rounded-full bg-transparent py-0 pl-1 text-small"
      :class="set ? 'pr-1' : 'pr-3'" data-test="chip-select"
      @change="emit('update:modelValue', ($event.target as HTMLSelectElement).value)"
    >
      <option :value="unsetValue">{{ any }}</option>
      <option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option>
    </select>
    <button
      v-if="set" type="button" class="mr-1 grid size-7 place-items-center rounded-full hover:bg-hover max-md:size-9"
      :aria-label="FILTERS.remove(label)" data-test="chip-remove" @click="emit('update:modelValue', unsetValue)"
    >
      <span aria-hidden="true">×</span>
    </button>
  </span>
</template>
