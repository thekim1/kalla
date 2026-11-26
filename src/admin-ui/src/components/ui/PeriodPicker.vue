<script setup lang="ts">
import { FILTERS } from '@/utils/labels'
import FilterChip from './FilterChip.vue'

/**
 * The period chip of a filter row (layout rules in docs/admin-ui.md, rule 2): *Last 7 days*, *Last 30 days* (the default), *This
 * month* and *Custom…*, which adds the range as two labelled date fields in the UI's date format.
 */
defineProps<{ period: string; from: string; to: string; idPrefix: string }>()
const emit = defineEmits<{ (e: 'update:period' | 'update:from' | 'update:to', value: string): void }>()
const options = [
  { value: '7d', label: FILTERS.periods['7d']! },
  { value: 'month', label: FILTERS.periods.month! },
  { value: 'custom', label: FILTERS.periods.custom! },
]
</script>

<template>
  <span class="inline-flex flex-wrap items-center gap-2" data-test="period-picker">
    <FilterChip :label="FILTERS.period" :model-value="period" :options="options" :any="FILTERS.periods['30d']!" unset="30d" @update:model-value="emit('update:period', $event)" />
    <template v-if="period === 'custom'">
      <label class="inline-flex items-center gap-1 text-small" data-test="period-from">
        <span>{{ FILTERS.from }}</span>
        <input :id="`${idPrefix}-from`" type="date" class="field-input !h-ctl w-auto max-md:!h-11" :value="from" :max="to || undefined" @change="emit('update:from', ($event.target as HTMLInputElement).value)" />
      </label>
      <label class="inline-flex items-center gap-1 text-small" data-test="period-to">
        <span>{{ FILTERS.to }}</span>
        <input :id="`${idPrefix}-to`" type="date" class="field-input !h-ctl w-auto max-md:!h-11" :value="to" :min="from || undefined" @change="emit('update:to', ($event.target as HTMLInputElement).value)" />
      </label>
    </template>
  </span>
</template>
