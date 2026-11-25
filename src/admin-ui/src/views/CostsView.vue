<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { CostGranularity, CostReport, CostReportQuery, CostTotals, DepartmentCostRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import PeriodPicker from '@/components/ui/PeriodPicker.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { IS_DAY, IS_ID, ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { groupTotals, isoDay, stepLabel, sumTotals, totalCost } from '@/components/costs/costs'
import { formatNumber, quietZero, formatUsdCost } from '@/utils/format'
import { COSTS, FILTERS, PAGE } from '@/utils/labels'
import { PERIODS, periodRange } from '@/utils/period'
import { COST_MESSAGES } from '@/utils/messages'

// Costs (admin design §8.6, FOLLOW-UP; department and platform admins): per department, one row per assistant and
// knowledge base with its cost, highest first, expandable by step, department totals, and the CSV with the same filters.
// The gateway's cost and the estimate are shown apart; the gateway is the record for charging. The page shows neither the
// period nor the client, so it asks by month (fewer rows to sum, #710). The filters are one row of chips kept in the
// address (§5.12 *Filters*; `?period=7d&departmentId=…&kind=assistant`), and *Export* in the header downloads the CSV
// with the same filters, by day or by month.
interface ObjectRow {
  key: string
  kind: DepartmentCostRow['kind']
  objectId: string
  name: string
  totals: CostTotals
  steps: { key: string; label: string; totals: CostTotals }[]
}

interface DepartmentGroup {
  id: string
  name: string
  totals: CostTotals
  objects: ObjectRow[]
}

const report = useLoadable<CostReport | null>(null)
const departments = ref<{ id: string; name: string }[]>([])
const { filters, active, clear } = useQueryFilters(
  { period: '30d', from: '', to: '', departmentId: '', kind: '' },
  { period: ONE_OF(PERIODS), from: IS_DAY, to: IS_DAY, departmentId: IS_ID, kind: ONE_OF(['assistant', 'collection']) },
)
const filterRow = ref<HTMLElement | null>(null)
/** The server reads an empty end as its own default, so an open custom range ends today here, as it is shown (#1051). */
const range = computed(() => {
  const r = periodRange(filters.period, filters.from, filters.to)
  return { from: r.from, to: r.to || isoDay(new Date()) }
})
const open = ref(new Set<string>())

const departmentOptions = computed(() => departments.value.map((d) => ({ value: d.id, label: d.name })))
const kindOptions = Object.entries(COSTS.kinds).filter(([value]) => value).map(([value, label]) => ({ value, label }))

/** The on-screen report's query: by month whatever the granularity, so a granularity change doesn't ask again. */
const query = computed<CostReportQuery>(() => ({
  from: range.value.from || undefined,
  to: range.value.to || undefined,
  granularity: 'month',
  departmentId: filters.departmentId || undefined,
  kind: (filters.kind || undefined) as CostReportQuery['kind'],
}))
/** Day rows cover at most 400 days on the server (period_too_long), so a longer range downloads by month. */
const csvByMonthOnly = computed(() => {
  const { from, to } = range.value
  if (!from || !to) return false
  return (Date.parse(to) - Date.parse(from)) / 86_400_000 + 1 > 400
})
const csvUrl = (granularity: CostGranularity) => api.costs.csvUrl({ ...query.value, granularity })
/** The objects the report lists, for the count at the end of the filter row. */
const objectCount = computed(() => groups.value.reduce((n, d) => n + d.objects.length, 0))

function objectName(row: DepartmentCostRow): string {
  return row.deleted || !row.objectName ? COSTS.deleted(row.objectId) : row.objectName
}

/** The rows per department and object, grouped in one pass. */
const groups = computed<DepartmentGroup[]>(() => {
  const byDepartment = new Map<string, { rows: DepartmentCostRow[]; objects: Map<string, DepartmentCostRow[]> }>()
  for (const r of report.data.value?.rows ?? []) {
    let department = byDepartment.get(r.departmentId)
    if (!department) {
      department = { rows: [], objects: new Map() }
      byDepartment.set(r.departmentId, department)
    }
    department.rows.push(r)
    const key = `${r.kind}:${r.objectId}`
    const own = department.objects.get(key)
    if (own) own.push(r)
    else department.objects.set(key, [r])
  }
  return [...byDepartment.entries()]
    .map(([id, d]) => {
      const objects = [...d.objects.entries()]
        .map<ObjectRow>(([key, own]) => {
          const first = own[0]!
          return {
            key, kind: first.kind, objectId: first.objectId, name: objectName(first), totals: sumTotals(own),
            steps: groupTotals(own, (r) => r.step).map((s) => ({ ...s, label: stepLabel(s.key) })),
          }
        })
        .sort((a, b) => totalCost(b.totals) - totalCost(a.totals) || a.key.localeCompare(b.key))
      return { id, name: d.rows[0]?.departmentName ?? id, totals: sumTotals(d.rows), objects }
    })
    .sort((a, b) => totalCost(b.totals) - totalCost(a.totals) || a.name.localeCompare(b.name))
})
const totals = computed(() => report.data.value?.totals ?? sumTotals([]))

function toggle(key: string): void {
  const next = new Set(open.value)
  if (next.has(key)) next.delete(key)
  else next.add(key)
  open.value = next
}

async function load(): Promise<void> {
  await report.run(() => api.costs.report(query.value))
}

watch(query, () => void load())
onMounted(async () => {
  await load()
  departments.value = await api.organisation.departments().then((ds) => ds.map((d) => ({ id: d.id, name: d.name }))).catch(() => [])
})
</script>

<template>
  <PageHeader :title="PAGE.costs.title" :description="PAGE.costs.description">
    <!-- Export in the header (§5.12 Filters): the CSV with the filters below, by day or by month. -->
    <span class="flex flex-wrap items-center gap-2" role="group" :aria-label="COSTS.export" data-test="costs-export">
      <span class="text-small font-medium text-fg-2" aria-hidden="true">{{ COSTS.export }}</span>
      <a
        v-if="!csvByMonthOnly" :href="csvUrl('day')" class="btn h-ctl bg-surface px-3 text-fg shadow-inset-strong hover:bg-surface-2" download
        :aria-label="COSTS.exportBy(COSTS.byDay)" data-test="costs-csv-day"
      >{{ COSTS.byDay }}</a>
      <a
        :href="csvUrl('month')" class="btn h-ctl bg-surface px-3 text-fg shadow-inset-strong hover:bg-surface-2" download
        :aria-label="COSTS.exportBy(COSTS.byMonth)" data-test="costs-csv-month"
      >{{ COSTS.byMonth }}</a>
    </span>
  </PageHeader>
  <p v-if="csvByMonthOnly" class="-mt-4 mb-4 text-small text-fg-2" data-test="costs-csv-month-only">{{ COSTS.csvMonthOnly }}</p>

  <!-- One row of chips (admin design §5.12 Filters), kept in the address; the count at its end, politely announced. -->
  <form ref="filterRow" class="mb-6 flex flex-wrap items-center gap-2" role="search" :aria-label="COSTS.filters" data-test="filters" @submit.prevent="load()">
    <PeriodPicker v-model:period="filters.period" v-model:from="filters.from" v-model:to="filters.to" id-prefix="costs" />
    <FilterChip v-model="filters.departmentId" :label="COSTS.department" :options="departmentOptions" :any="COSTS.allDepartments" />
    <FilterChip v-model="filters.kind" :label="COSTS.kind" :options="kindOptions" :any="COSTS.kinds['']!" />
    <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clear(filterRow)">{{ FILTERS.clear }}</UiButton>
    <p class="ml-auto text-small text-fg-2" aria-live="polite" data-test="filter-count">{{ report.loaded.value ? COSTS.shown(objectCount) : '' }}</p>
  </form>

  <AsyncState :loading="report.loading.value" :error="report.error.value" :empty="groups.length === 0" :empty-text="COST_MESSAGES.empty" @retry="load()">
    <div class="grid gap-6">
      <UiCard v-for="d in groups" :key="d.id" :title="d.name" :meta="formatUsdCost(totalCost(d.totals))" data-test="costs-department">
        <table class="w-full text-left">
          <caption class="sr-only">{{ COSTS.caption }}</caption>
          <thead>
            <tr class="text-small text-fg-2">
              <th scope="col" class="py-2">{{ COSTS.object }}</th>
              <th scope="col" class="py-2">{{ COSTS.kind }}</th>
              <th scope="col" class="py-2 text-right">{{ COSTS.calls }}</th>
              <th scope="col" class="py-2 text-right">{{ COSTS.gateway }}</th>
              <th scope="col" class="py-2 text-right">{{ COSTS.estimated }}</th>
              <th scope="col" class="py-2 text-right">{{ COSTS.total }}</th>
            </tr>
          </thead>
          <tbody>
            <template v-for="o in d.objects" :key="o.key">
              <tr class="border-t border-line" :data-test="`costs-object-${o.objectId}`">
                <th scope="row" class="py-2 font-medium">
                  <UiButton size="sm" variant="quiet" :aria-expanded="open.has(o.key)" :aria-label="`${open.has(o.key) ? COSTS.hideSteps : COSTS.showSteps}: ${o.name}`"
                            :data-test="`costs-toggle-${o.objectId}`" @click="toggle(o.key)"
>{{ o.name }}</UiButton>
                </th>
                <td class="py-2">{{ o.kind === 'assistant' ? COSTS.kindAssistant : COSTS.kindCollection }}</td>
                <td class="py-2 text-right tabular" :class="quietZero(o.totals.calls)">{{ formatNumber(o.totals.calls) }}</td>
                <td class="py-2 text-right tabular" :class="quietZero(o.totals.costUsd)">{{ formatUsdCost(o.totals.costUsd) }}</td>
                <td class="py-2 text-right tabular" :class="quietZero(o.totals.estimatedCostUsd)">{{ formatUsdCost(o.totals.estimatedCostUsd) }}</td>
                <td class="py-2 text-right tabular font-medium" :class="quietZero(totalCost(o.totals))">{{ formatUsdCost(totalCost(o.totals)) }}</td>
              </tr>
              <template v-if="open.has(o.key)">
                <tr v-for="s in o.steps" :key="`${o.key}:${s.key}`" class="text-small text-fg-2" :data-test="`costs-step-${o.objectId}-${s.key}`">
                  <td class="py-1 pl-6">{{ s.label }}</td>
                  <td />
                  <td class="py-1 text-right tabular" :class="quietZero(s.totals.calls)">{{ formatNumber(s.totals.calls) }}</td>
                  <td class="py-1 text-right tabular" :class="quietZero(s.totals.costUsd)">{{ formatUsdCost(s.totals.costUsd) }}</td>
                  <td class="py-1 text-right tabular" :class="quietZero(s.totals.estimatedCostUsd)">{{ formatUsdCost(s.totals.estimatedCostUsd) }}</td>
                  <td class="py-1 text-right tabular" :class="quietZero(totalCost(s.totals))">{{ formatUsdCost(totalCost(s.totals)) }}</td>
                </tr>
              </template>
            </template>
          </tbody>
          <tfoot>
            <tr class="border-t border-line font-medium" data-test="costs-department-total">
              <th scope="row" colspan="2" class="py-2">{{ COSTS.departmentTotal(d.name) }}</th>
              <td class="py-2 text-right tabular" :class="quietZero(d.totals.calls)">{{ formatNumber(d.totals.calls) }}</td>
              <td class="py-2 text-right tabular" :class="quietZero(d.totals.costUsd)">{{ formatUsdCost(d.totals.costUsd) }}</td>
              <td class="py-2 text-right tabular" :class="quietZero(d.totals.estimatedCostUsd)">{{ formatUsdCost(d.totals.estimatedCostUsd) }}</td>
              <td class="py-2 text-right tabular" :class="quietZero(totalCost(d.totals))">{{ formatUsdCost(totalCost(d.totals)) }}</td>
            </tr>
          </tfoot>
        </table>
      </UiCard>
      <p v-if="groups.length > 1" class="font-medium" data-test="costs-total">{{ COSTS.grandTotal }}: <span class="tabular">{{ formatUsdCost(totalCost(totals)) }}</span></p>
    </div>
  </AsyncState>
  <p v-if="totals.estimatedCalls > 0" class="mt-4 text-small text-fg-2" data-test="costs-estimated-note">{{ COST_MESSAGES.estimatedNote(formatNumber(totals.estimatedCalls)) }}</p>
  <p class="mt-2 text-small text-fg-2" data-test="costs-gateway-note">{{ COST_MESSAGES.gatewayRecord }}</p>
</template>
