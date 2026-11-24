<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { Department, DepartmentMap, MapRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useQueryFilters } from '@/composables/useQueryFilters'
import { useLoadable } from '@/stores/loadable'
import { FILTERS, MAP, PAGE, SHARING_LABEL } from '@/utils/labels'

// The Map as a table (admin design §5.5.6, P1b): one row per department → knowledge base → assistant → channel path,
// with the responsible contact, filtered by department and by what to show: chips kept in the address (§5.12 *Filters*;
// `?department=…&show=shared`). The drawing (B-67, with its Table / Drawing switch on the same row) comes later.
type Show = 'all' | 'shared' | 'unlinked'
interface Row extends MapRow {
  key: string
  department: string
}
const departments = ref<Department[]>([])
const { filters, active, clear } = useQueryFilters({ department: '', show: 'all' })
const maps = useLoadable<DepartmentMap[]>([])
const departmentOptions = computed(() => departments.value.map((d) => ({ value: d.id, label: d.name })))
const showOptions = (['shared', 'unlinked'] as Show[]).map((v) => ({ value: v, label: MAP.showOptions[v]! }))
const columns: Column[] = [
  { key: 'department', label: MAP.department },
  { key: 'collection', label: MAP.knowledgeBase },
  { key: 'sharing', label: MAP.shared },
  { key: 'assistant', label: MAP.assistant },
  { key: 'channel', label: MAP.channel },
  { key: 'responsible', label: MAP.responsible },
]
const rows = computed<Row[]>(() =>
  maps.data.value
    .flatMap((m) => m.rows.map((r, i) => ({ ...r, department: m.departmentName, key: `${m.departmentId}-${i}` })))
    .filter((r) => filters.show !== 'shared' && filters.show !== 'unlinked'
      || (filters.show === 'shared' ? r.collection.shared || r.collection.sharedWith !== 'department' : r.assistant === null)),
)

/** Own knowledge bases show how they are shared; a shared-in one shows its owner instead. */
const sharing = (r: Row) => (r.collection.shared || r.collection.sharedWith === 'department' ? MAP.none : SHARING_LABEL[r.collection.sharedWith])
const responsible = (r: Row) => (r.assistant ? r.assistant.contactName ?? r.assistant.contactEmail : r.collection.contactName ?? r.collection.contactEmail) ?? MAP.none

/** "version 3 · topics: …", "draft" or "not published". */
function assistantDetails(r: Row): string {
  const a = r.assistant!
  const state = a.draftOnly ? MAP.draftOnly : a.publishedVersion ? MAP.version(a.publishedVersion) : MAP.notPublished
  return a.topics.length ? `${state} · ${MAP.topics(a.topics.join(', '))}` : state
}

async function load(): Promise<void> {
  const ids = filters.department ? [filters.department] : departments.value.map((d) => d.id)
  await maps.run(() => Promise.all(ids.map((id) => api.organisation.map(id))))
}

/** A row opens its assistant, or the knowledge base when no assistant uses it. */
const rowTo = (row: Row) => (row.assistant ? { name: 'assistant', params: { id: row.assistant.id } } : { name: 'collection', params: { id: row.collection.id } })

watch(() => filters.department, () => void load())

onMounted(async () => {
  departments.value = await api.organisation.departments().catch(() => [])
  await load()
})
</script>

<template>
  <PageHeader :title="PAGE.map.title" :description="MAP.lead" />
  <!-- One row of chips (admin design §5.12 Filters), kept in the address; the count at its end, politely announced. -->
  <form class="mb-4 flex flex-wrap items-center gap-2" role="search" :aria-label="MAP.filters" data-test="filters" @submit.prevent>
    <FilterChip v-if="departments.length > 1" v-model="filters.department" :label="MAP.department" :options="departmentOptions" :any="MAP.allDepartments" />
    <FilterChip v-model="filters.show" :label="MAP.show" :options="showOptions" :any="MAP.showOptions.all!" unset="all" />
    <UiButton v-if="active.length" size="sm" variant="quiet" data-test="clear-filters" @click="clear()">{{ FILTERS.clear }}</UiButton>
    <p class="ml-auto text-small text-fg-2" aria-live="polite" data-test="filter-count">{{ maps.loaded.value ? MAP.shown(rows.length) : '' }}</p>
  </form>
  <AsyncState :loading="maps.loading.value" :error="maps.error.value" :empty="rows.length === 0" :empty-text="MAP.empty" @retry="load">
    <DataTable :columns="columns" :rows="rows" :row-key="(r) => r.key" :caption="MAP.caption" :row-to="rowTo" data-test="map">
      <template #cell-department="{ row }">{{ row.department }}</template>
      <template #cell-collection="{ row }">
        <span class="font-medium">{{ row.collection.name }}</span>
        <span v-if="row.collection.shared" class="block text-small text-fg-2">{{ MAP.sharedIn(row.collection.departmentName) }}</span>
      </template>
      <template #cell-sharing="{ row }">{{ sharing(row as Row) }}</template>
      <template #cell-assistant="{ row }">
        <template v-if="row.assistant">
          <span class="font-medium">{{ row.assistant.name }}</span>
          <span class="block text-small text-fg-2">{{ assistantDetails(row as Row) }}</span>
        </template>
        <span v-else class="text-fg-3">{{ MAP.none }}</span>
      </template>
      <template #cell-channel="{ row }">{{ row.audience?.name ?? MAP.none }}</template>
      <template #cell-responsible="{ row }">{{ responsible(row as Row) }}</template>
    </DataTable>
  </AsyncState>
</template>
