<script setup lang="ts" generic="Row extends Record<string, any>">
import { useRouter, type RouteLocationRaw } from 'vue-router'
import { COMMON } from '@/utils/labels'
import AppIcon from './AppIcon.vue'

export interface Column {
  key: string
  label: string
  align?: 'right'
  /** Visually hide the header text (e.g. actions column). */
  hideLabel?: boolean
  class?: string
  /** Header becomes a button that emits `sort` with this column's key. */
  sortable?: boolean
}

const props = defineProps<{
  columns: Column[]
  rows: Row[]
  rowKey: (row: Row) => string
  caption: string
  /**
   * Rows that open a page (admin design §5.5.2 "Lists open as links"): the first cell is a real link to this location, so a
   * row opens in a new tab and its address can be copied; a click elsewhere on the row opens it too.
   */
  rowTo?: (row: Row) => RouteLocationRaw
  /** Rows that open a drawer: the first cell is a real button and the whole row emits `row-click`. */
  clickable?: boolean
  selectedKey?: string | null
  emptyText?: string
  /** Shows a "Clear filters" action in the empty state. */
  clearable?: boolean
  sortKey?: string
  sortDir?: 'asc' | 'desc'
}>()
const emit = defineEmits<{ (e: 'row-click', row: Row): void; (e: 'clear'): void; (e: 'sort', key: string): void }>()

function ariaSort(col: Column): 'ascending' | 'descending' | 'none' | undefined {
  if (!col.sortable) return undefined
  if (props.sortKey !== col.key) return 'none'
  return props.sortDir === 'desc' ? 'descending' : 'ascending'
}

// Only a list of links needs the router (most tables are mounted without one in specs and drawers).
const router = props.rowTo ? useRouter() : null

function open(row: Row, event: MouseEvent): void {
  // A link in the row (the first cell's or another cell's) navigates by itself: no second navigation.
  if ((event.target as Element | null)?.closest('a')) return
  if (props.rowTo) void router?.push(props.rowTo(row))
  else if (props.clickable) emit('row-click', row)
}
</script>

<template>
  <div class="material overflow-hidden rounded-card">
    <table class="data-table w-full text-body max-md:block">
      <caption class="sr-only">{{ caption }}</caption>
      <thead class="bg-surface-2 text-caption text-fg-3 max-md:sr-only">
        <tr>
          <th
            v-for="(col, i) in columns"
            :key="col.key"
            scope="col"
            class="h-9 px-3 font-medium first:px-5 last:px-5"
            :class="[col.align === 'right' ? 'text-right' : 'text-left', col.class]"
            :data-first="i === 0 || undefined"
            :aria-sort="ariaSort(col)"
          >
            <button
              v-if="col.sortable"
              type="button"
              class="-mx-1 inline-flex items-center gap-1 rounded-chip px-1 font-medium hover:text-fg"
              :class="{ 'text-fg': sortKey === col.key }"
              @click="emit('sort', col.key)"
            >
              {{ col.label }}
              <AppIcon name="arrow_downward" :size="16" :class="sortKey === col.key ? (sortDir === 'asc' ? 'rotate-180' : '') : 'opacity-30'" />
            </button>
            <span v-else :class="{ 'sr-only': col.hideLabel }">{{ col.label }}</span>
          </th>
        </tr>
      </thead>
      <tbody class="max-md:block">
        <tr
          v-for="row in rows"
          :key="rowKey(row)"
          class="h-row border-t border-border hover:bg-hover max-md:block max-md:h-auto max-md:px-4 max-md:py-3"
          :class="{ 'cursor-pointer': rowTo || clickable, 'bg-accent-soft': selectedKey === rowKey(row) }"
          @click="open(row, $event)"
        >
          <template v-for="(col, i) in columns" :key="col.key">
            <th
              v-if="i === 0"
              scope="row"
              class="px-5 py-2 text-left font-normal max-md:block max-md:px-0 max-md:py-0 max-md:pb-1"
            >
              <RouterLink v-if="rowTo" :to="rowTo(row)" class="-mx-1 inline-block rounded-chip px-1 text-left text-fg hover:text-fg hover:underline" data-test="row-link">
                <slot :name="`cell-${col.key}`" :row="row" />
              </RouterLink>
              <button v-else-if="clickable" type="button" class="-mx-1 rounded-chip px-1 text-left">
                <slot :name="`cell-${col.key}`" :row="row" />
              </button>
              <slot v-else :name="`cell-${col.key}`" :row="row" />
            </th>
            <td
              v-else
              class="px-3 py-2 align-middle last:px-5 max-md:flex max-md:items-baseline max-md:justify-between max-md:gap-4 max-md:px-0 max-md:py-1 max-md:last:px-0"
              :class="[col.align === 'right' ? 'tabular text-right max-md:text-right' : '', col.class]"
            >
              <span class="hidden text-caption text-fg-3 max-md:inline" :class="{ '!hidden': col.hideLabel }" aria-hidden="true">{{ col.label }}</span>
              <span class="min-w-0"><slot :name="`cell-${col.key}`" :row="row" /></span>
            </td>
          </template>
        </tr>
      </tbody>
    </table>
    <div v-if="rows.length === 0" class="border-t border-border px-5 py-8 text-center text-fg-2">
      <slot name="empty">
        {{ emptyText ?? COMMON.empty }}
        <button v-if="clearable" type="button" class="font-medium text-accent-ink underline" @click="emit('clear')">{{ COMMON.clearFilters }}</button>
      </slot>
    </div>
  </div>
</template>
