<script setup lang="ts">
import { COMMON, PAGINATION } from '@/utils/labels'
import { computed } from 'vue'
import { formatNumber } from '@/utils/format'
import UiButton from './UiButton.vue'

defineOptions({ name: 'UiPagination' })
const props = defineProps<{ page: number; pageSize: number; total: number; label: string }>()
const emit = defineEmits<{ (e: 'update:page', value: number): void }>()

const pageCount = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))
const first = computed(() => (props.total === 0 ? 0 : (props.page - 1) * props.pageSize + 1))
const last = computed(() => Math.min(props.total, props.page * props.pageSize))
</script>

<template>
  <nav :aria-label="label" class="mt-4 flex flex-wrap items-center justify-between gap-3">
    <p class="tabular text-small text-fg-2" aria-live="polite">{{ PAGINATION.range(formatNumber(first), formatNumber(last), formatNumber(total)) }}</p>
    <div class="flex items-center gap-2">
      <UiButton size="sm" icon="chevron_left" :disabled="page <= 1" @click="emit('update:page', page - 1)">{{ COMMON.previous }}</UiButton>
      <span class="tabular text-small text-fg-2">{{ PAGINATION.page(page, pageCount) }}</span>
      <UiButton size="sm" icon="chevron_right" :disabled="page >= pageCount" @click="emit('update:page', page + 1)">{{ COMMON.next }}</UiButton>
    </div>
  </nav>
</template>
