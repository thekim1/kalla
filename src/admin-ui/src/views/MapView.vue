<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { Department, DepartmentMap, MapRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import MapDrawing from '@/components/map/MapDrawing.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { IS_ID, ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { useLoadable } from '@/stores/loadable'
import { FILTERS, MAP, PAGE, SHARING_LABEL } from '@/utils/labels'

// The Map as a table (admin design §5.5.6, P1b): one row per department → source → knowledge base → assistant → channel
// path (a shared-in knowledge base's sources are its owner's, so its rows have none, #1083),
// with the responsible contact, filtered by department and by what to show: chips kept in the address (§5.12 *Filters*;
// `?department=…&show=shared`). **Table** / **Drawing** at the row's end (`?view=drawing`, B-67): the drawing is one card per
// department, with the same filters.
type Show = 'all' | 'shared' | 'unlinked'
interface Row extends MapRow {
  key: string
  department: string
  /** The source of this path; null for a knowledge base without one of the department's own sources. */
  source: MapRow['collection']['sources'][number] | null
}
const departments = ref<Department[]>([])
const { filters, active: set, clear } = useQueryFilters({ department: '', show: 'all' }, { department: IS_ID, show: ONE_OF(['all', 'shared', 'unlinked']) })
// The view is not a filter: Clear filters leaves it as it is.
const { filters: shape } = useQueryFilters({ view: 'table' }, { view: ONE_OF(['table', 'drawing']) })
const viewOptions = (['table', 'drawing'] as const).map((v) => ({ value: v, label: MAP.views[v] }))
const filterRow = ref<HTMLElement | null>(null)
const maps = useLoadable<DepartmentMap[]>([])
/** The filters that are set and shown: a single-department user has no department chip to clear. */
const active = computed(() => set.value.filter((k) => k !== 'department' || departments.value.length > 1))
const departmentOptions = computed(() => departments.value.map((d) => ({ value: d.id, label: d.name })))
const showOptions = (['shared', 'unlinked'] as Show[]).map((v) => ({ value: v, label: MAP.showOptions[v]! }))
const columns: Column[] = [
  { key: 'department', label: MAP.department },
  { key: 'source', label: MAP.source },
  { key: 'collection', label: MAP.knowledgeBase },
  { key: 'sharing', label: MAP.shared },
  { key: 'assistant', label: MAP.assistant },
  { key: 'channel', label: MAP.channel },
  { key: 'responsible', label: MAP.responsible },
]
/** What to show: everything, the shared knowledge bases (shared in or out), or those no assistant uses. */
const showing = (r: MapRow) => filters.show !== 'shared' && filters.show !== 'unlinked'
  || (filters.show === 'shared' ? r.collection.shared || r.collection.sharedWith !== 'department' : r.assistant === null)
const rows = computed<Row[]>(() =>
  maps.data.value.flatMap((m) => m.rows.filter(showing).flatMap((r, i) =>
    (r.collection.sources.length ? r.collection.sources : [null]).map((source) =>
      ({ ...r, source, department: m.departmentName, key: `${m.departmentId}-${i}-${source?.id ?? ''}` })))),
)
/** The drawing: one per department that has something to show. */
const drawings = computed(() => maps.data.value.map((m) => ({ id: m.departmentId, name: m.departmentName, rows: m.rows.filter(showing) })).filter((d) => d.rows.length))

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

/**
 * A department from the address that isn't one of the user's reads as all of them (the API would refuse it): on
 * arrival, and on every later change of the address (back, forward, a link while the page is open, #1072).
 */
const known = (department: string) => !department || departments.value.some((d) => d.id === department)
watch(() => filters.department, (department) => {
  if (!known(department)) filters.department = ''
  else void load()
})

onMounted(async () => {
  departments.value = await api.organisation.departments().catch(() => [])
  if (!known(filters.department)) filters.department = ''
  else await load()
})
</script>

<template>
  <PageHeader :title="PAGE.map.title" :description="MAP.lead" />
  <!-- One row of chips (admin design §5.12 Filters), kept in the address; the count at its end, politely announced. -->
  <form ref="filterRow" class="mb-4 flex flex-wrap items-center gap-2" role="search" :aria-label="MAP.filters" data-test="filters" @submit.prevent>
    <FilterChip v-if="departments.length > 1" v-model="filters.department" :label="MAP.department" :options="departmentOptions" :any="MAP.allDepartments" />
    <FilterChip v-model="filters.show" :label="MAP.show" :options="showOptions" :any="MAP.showOptions.all!" unset="all" />
    <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clear(filterRow)">{{ FILTERS.clear }}</UiButton>
    <p class="ml-auto text-small text-fg-2" aria-live="polite" data-test="filter-count">
      {{ maps.loaded.value ? (shape.view === 'drawing' ? MAP.drawing.shown(drawings.length) : MAP.shown(rows.length)) : '' }}
    </p>
    <SegmentedControl v-model="shape.view" :options="viewOptions" :label="MAP.view" data-test="map-view" />
  </form>
  <AsyncState :loading="maps.loading.value || (!maps.loaded.value && !maps.error.value)" :error="maps.error.value" :empty="rows.length === 0" :empty-text="MAP.empty" @retry="load">
    <div v-if="shape.view === 'drawing'" class="grid gap-6" data-test="map-drawings">
      <p class="m-0 text-small text-fg-2 max-md:hidden">{{ MAP.drawing.hint }}</p>
      <MapDrawing v-for="d in drawings" :key="d.id" :department-name="d.name" :rows="d.rows" />
    </div>
    <DataTable v-else :columns="columns" :rows="rows" :row-key="(r) => r.key" :caption="MAP.caption" :row-to="rowTo" data-test="map">
      <template #cell-department="{ row }">{{ row.department }}</template>
      <template #cell-source="{ row }">
        <span v-if="row.source" class="[overflow-wrap:anywhere]" data-test="map-source">{{ row.source.name }}</span>
        <span v-else class="text-fg-3">{{ MAP.none }}</span>
      </template>
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
