<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api } from '@/api'
import type { CostGranularity, CostReport, CostReportQuery, CostTotals, DepartmentCostRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { groupTotals, lastDays, stepLabel, sumTotals, totalCost } from '@/components/costs/costs'
import { formatNumber, formatUsdCost } from '@/utils/format'
import { COSTS, PAGE } from '@/utils/labels'
import { COST_MESSAGES } from '@/utils/messages'

// Costs (admin design §8.6, FOLLOW-UP; department and platform admins): per department, one row per assistant and
// knowledge base with its cost, highest first, expandable by step, department totals, and the CSV with the same filters.
// The gateway's cost and the estimate are shown apart; the gateway is the record for charging.
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

const route = useRoute()
const report = useLoadable<CostReport | null>(null)
const departments = ref<{ id: string; name: string }[]>([])
const initial = lastDays(30)
const filters = reactive({
  departmentId: typeof route.query.departmentId === 'string' ? route.query.departmentId : '',
  kind: '' as '' | 'assistant' | 'collection',
  from: initial.from,
  to: initial.to,
  granularity: 'day' as CostGranularity,
})
const open = ref(new Set<string>())

const departmentOptions = computed(() => [{ value: '', label: COSTS.allDepartments }, ...departments.value.map((d) => ({ value: d.id, label: d.name }))])
const kindOptions = Object.entries(COSTS.kinds).map(([value, label]) => ({ value, label }))
const granularityOptions = (['day', 'month'] as const).map((value) => ({ value, label: COSTS.granularities[value]! }))

const query = computed<CostReportQuery>(() => ({
  from: filters.from || undefined,
  to: filters.to || undefined,
  granularity: filters.granularity,
  departmentId: filters.departmentId || undefined,
  kind: filters.kind || undefined,
}))
const csvUrl = computed(() => api.costs.csvUrl(query.value))

function objectName(row: DepartmentCostRow): string {
  return row.deleted || !row.objectName ? COSTS.deleted(row.objectId) : row.objectName
}

const groups = computed<DepartmentGroup[]>(() => {
  const rows = report.data.value?.rows ?? []
  const byDepartment = new Map<string, DepartmentCostRow[]>()
  for (const r of rows) byDepartment.set(r.departmentId, [...(byDepartment.get(r.departmentId) ?? []), r])
  return [...byDepartment.entries()]
    .map(([id, rs]) => {
      const objects = groupTotals(rs, (r) => `${r.kind}:${r.objectId}`).map<ObjectRow>((g) => {
        const own = rs.filter((r) => `${r.kind}:${r.objectId}` === g.key)
        const first = own[0]!
        return {
          key: g.key, kind: first.kind, objectId: first.objectId, name: objectName(first), totals: g.totals,
          steps: groupTotals(own, (r) => r.step).map((s) => ({ ...s, label: stepLabel(s.key) })),
        }
      })
      return { id, name: rs[0]?.departmentName ?? id, totals: sumTotals(rs), objects }
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

watch(filters, () => void load())
onMounted(async () => {
  await load()
  departments.value = await api.organisation.departments().then((ds) => ds.map((d) => ({ id: d.id, name: d.name }))).catch(() => [])
})
</script>

<template>
  <PageHeader :title="PAGE.costs.title" :description="PAGE.costs.description" />

  <form class="mb-6 flex flex-wrap items-end gap-4" role="search" :aria-label="COSTS.filters" @submit.prevent="load()">
    <FilterSelect v-model="filters.departmentId" :label="COSTS.department" :options="departmentOptions" />
    <FilterSelect v-model="filters.kind" :label="COSTS.kind" :options="kindOptions" />
    <TextField id="costs-from" v-model="filters.from" type="date" :label="COSTS.from" />
    <TextField id="costs-to" v-model="filters.to" type="date" :label="COSTS.to" />
    <SegmentedControl v-model="filters.granularity" :label="COSTS.granularity" :options="granularityOptions" />
    <a :href="csvUrl" class="link text-small" download data-test="costs-csv">{{ COSTS.downloadCsv }}</a>
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
                <td class="py-2 text-right tabular">{{ formatNumber(o.totals.calls) }}</td>
                <td class="py-2 text-right tabular">{{ formatUsdCost(o.totals.costUsd) }}</td>
                <td class="py-2 text-right tabular">{{ formatUsdCost(o.totals.estimatedCostUsd) }}</td>
                <td class="py-2 text-right tabular font-medium">{{ formatUsdCost(totalCost(o.totals)) }}</td>
              </tr>
              <template v-if="open.has(o.key)">
                <tr v-for="s in o.steps" :key="`${o.key}:${s.key}`" class="text-small text-fg-2" :data-test="`costs-step-${o.objectId}-${s.key}`">
                  <td class="py-1 pl-6">{{ s.label }}</td>
                  <td />
                  <td class="py-1 text-right tabular">{{ formatNumber(s.totals.calls) }}</td>
                  <td class="py-1 text-right tabular">{{ formatUsdCost(s.totals.costUsd) }}</td>
                  <td class="py-1 text-right tabular">{{ formatUsdCost(s.totals.estimatedCostUsd) }}</td>
                  <td class="py-1 text-right tabular">{{ formatUsdCost(totalCost(s.totals)) }}</td>
                </tr>
              </template>
            </template>
          </tbody>
          <tfoot>
            <tr class="border-t border-line font-medium" data-test="costs-department-total">
              <th scope="row" colspan="2" class="py-2">{{ COSTS.departmentTotal(d.name) }}</th>
              <td class="py-2 text-right tabular">{{ formatNumber(d.totals.calls) }}</td>
              <td class="py-2 text-right tabular">{{ formatUsdCost(d.totals.costUsd) }}</td>
              <td class="py-2 text-right tabular">{{ formatUsdCost(d.totals.estimatedCostUsd) }}</td>
              <td class="py-2 text-right tabular">{{ formatUsdCost(totalCost(d.totals)) }}</td>
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
