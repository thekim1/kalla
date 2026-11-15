<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { CostGranularity, CostQuery, GatewayKeyList, KeyUsage, ObjectCosts } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import SegmentedControl from '@/components/ui/SegmentedControl.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { clientLabel, groupTotals, INDEXING_STEPS, lastDays, stepLabel, sumTotals, totalCost } from '@/components/costs/costs'
import { formatNumber, formatUsdCost } from '@/utils/format'
import { gatewayHref } from '@/utils/gateway'
import { COSTS } from '@/utils/labels'
import { COST_MESSAGES } from '@/utils/messages'

// The Cost card of admin design §8.6: an assistant's (Statistics tab, with a period picker, by step and by client) or a
// knowledge base's (Overview, the last 30 days, indexing and evaluation apart). The total shows the gateway's cost and the
// estimate of the calls without one apart.
const props = defineProps<{
  kind: 'assistant' | 'collection'
  source: (query: CostQuery) => Promise<ObjectCosts>
  /** A route to the Costs page for those who see it (the knowledge base's card, admin §8.6). */
  costsLink?: { name: string; query: Record<string, string> } | null
  /** B-46: the object's gateway key, whose figures (the gateway's Integration API, B-45) are shown beside Ume.Rag's. */
  gatewayKeyId?: string | null
  /** The object's department, for the gateway link when there is no read-back. */
  departmentId?: string
}>()

const costs = useLoadable<ObjectCosts | null>(null)
/** The gateway's figures for the key, same period; null without the read-back (then the gateway link is shown). */
const gateway = ref<KeyUsage | null>(null)
/** The department's keys, read once: the gateway link's address and how many objects hold the key (#642). */
const keyList = ref<GatewayKeyList | null>(null)
const days = ref('30')
const granularity = ref<CostGranularity>('day')
const periodOptions = Object.entries(COSTS.periods).map(([value, label]) => ({ value, label }))
const granularityOptions = (['day', 'month'] as const).map((value) => ({ value, label: COSTS.granularities[value]! }))

async function load(): Promise<void> {
  const query = { ...lastDays(Number(days.value)), granularity: granularity.value }
  const key = props.gatewayKeyId
  const departmentId = props.departmentId
  const [, usage, list] = await Promise.all([
    costs.run(() => props.source(query)),
    key ? api.gateway.keyUsage(key, query).catch(() => null) : Promise.resolve(null),
    key && departmentId && keyList.value === null ? api.gateway.keys(departmentId).catch(() => null) : Promise.resolve(keyList.value),
  ])
  gateway.value = usage
  keyList.value = list
}

/** The gateway's usage report, linked when there is no read-back. */
const gatewayLink = computed(() => gatewayHref(keyList.value?.adminUrl, '/usage'))
/** Admin §8.6: the note only when several assistants or knowledge bases share the key, so its figures include theirs. */
const sharedKey = computed(() => (keyList.value?.keys.find((k) => k.id === props.gatewayKeyId)?.usedBy ?? 0) > 1)

/** The gateway's cost per step for the key (its rows summed over the period). */
const gatewayBySteps = computed(() => {
  const sums = new Map<string, number>()
  for (const r of gateway.value?.rows ?? []) sums.set(r.step ?? '', (sums.get(r.step ?? '') ?? 0) + r.costUsd)
  return sums
})
const gatewayTotal = computed(() => (gateway.value?.rows ?? []).reduce((t, r) => t + r.costUsd, 0))
const gatewayRequests = computed(() => (gateway.value?.rows ?? []).reduce((t, r) => t + r.requests, 0))

const rows = computed(() => costs.data.value?.rows ?? [])
const totals = computed(() => costs.data.value?.totals ?? sumTotals([]))
const bySteps = computed(() => groupTotals(rows.value, (r) => r.step).map((g) => ({ ...g, label: stepLabel(g.key) })))
const byClients = computed(() => groupTotals(rows.value, (r) => r.clientName ?? '').map((g) => ({ ...g, label: clientLabel(g.key) })))
const indexing = computed(() => sumTotals(rows.value.filter((r) => INDEXING_STEPS.includes(r.step))))
const evaluation = computed(() => sumTotals(rows.value.filter((r) => !INDEXING_STEPS.includes(r.step))))

const baseColumns: Column[] = [
  { key: 'label', label: COSTS.step },
  { key: 'calls', label: COSTS.calls, align: 'right' },
  { key: 'tokens', label: COSTS.tokens, align: 'right' },
  { key: 'cost', label: COSTS.cost, align: 'right' },
]
const stepColumns = computed<Column[]>(() => (gateway.value ? [...baseColumns, { key: 'gateway', label: COSTS.gatewayCounted, align: 'right' }] : baseColumns))
const clientColumns: Column[] = [{ ...baseColumns[0]!, label: COSTS.client }, ...baseColumns.slice(1)]

watch([days, granularity], () => void load())
onMounted(load)
</script>

<template>
  <UiCard :title="COSTS.card" data-test="cost-card">
    <template v-if="kind === 'assistant'" #actions>
      <div class="flex flex-wrap items-center gap-3">
        <FilterSelect v-model="days" :label="COSTS.period" :options="periodOptions" />
        <SegmentedControl v-model="granularity" :label="COSTS.granularity" :options="granularityOptions" />
      </div>
    </template>
    <AsyncState :loading="costs.loading.value" :error="costs.error.value" :empty="!costs.data.value" @retry="load()">
      <p v-if="!rows.length" class="text-small text-fg-2" data-test="cost-empty">{{ COST_MESSAGES.empty }}</p>
      <template v-else>
        <p class="text-heading tabular" data-test="cost-total">{{ formatUsdCost(totalCost(totals)) }}</p>
        <p class="mb-4 text-small text-fg-2" data-test="cost-parts">
          {{ COSTS.gateway }}: <span class="tabular">{{ formatUsdCost(totals.costUsd) }}</span> ·
          {{ COSTS.estimated }}: <span class="tabular">{{ formatUsdCost(totals.estimatedCostUsd) }}</span>
        </p>
        <dl v-if="kind === 'collection'" class="mb-4 grid gap-2 sm:grid-cols-2" data-test="cost-split">
          <div><dt class="text-small text-fg-2">{{ COSTS.indexing }}</dt><dd class="tabular" data-test="cost-indexing">{{ formatUsdCost(totalCost(indexing)) }}</dd></div>
          <div><dt class="text-small text-fg-2">{{ COSTS.evaluation }}</dt><dd class="tabular" data-test="cost-evaluation">{{ formatUsdCost(totalCost(evaluation)) }}</dd></div>
        </dl>
        <h3 class="mb-2 font-medium">{{ COSTS.byStep }}</h3>
        <DataTable :columns="stepColumns" :rows="bySteps" :row-key="(r) => r.key" :caption="COSTS.byStep" data-test="cost-steps">
          <template #cell-label="{ row }">{{ row.label }}</template>
          <template #cell-calls="{ row }"><span class="tabular">{{ formatNumber(row.totals.calls) }}</span></template>
          <template #cell-tokens="{ row }"><span class="tabular">{{ formatNumber(row.totals.inputTokens + row.totals.outputTokens) }}</span></template>
          <template #cell-cost="{ row }"><span class="tabular">{{ formatUsdCost(totalCost(row.totals)) }}</span></template>
          <template #cell-gateway="{ row }"><span class="tabular">{{ formatUsdCost(gatewayBySteps.get(row.key) ?? 0) }}</span></template>
        </DataTable>
        <template v-if="kind === 'assistant'">
          <h3 class="mb-2 mt-4 font-medium">{{ COSTS.byClient }}</h3>
          <DataTable :columns="clientColumns" :rows="byClients" :row-key="(r) => r.key" :caption="COSTS.byClient" data-test="cost-clients">
            <template #cell-label="{ row }">{{ row.label }}</template>
            <template #cell-calls="{ row }"><span class="tabular">{{ formatNumber(row.totals.calls) }}</span></template>
            <template #cell-tokens="{ row }"><span class="tabular">{{ formatNumber(row.totals.inputTokens + row.totals.outputTokens) }}</span></template>
            <template #cell-cost="{ row }"><span class="tabular">{{ formatUsdCost(totalCost(row.totals)) }}</span></template>
          </DataTable>
        </template>
        <div v-if="gateway" class="mt-3 text-small" data-test="cost-gateway">
          <p>{{ COST_MESSAGES.gatewayFigures(formatUsdCost(gatewayTotal), formatNumber(gatewayRequests)) }}</p>
          <p v-if="sharedKey" class="text-fg-3" data-test="cost-shared-key">{{ COST_MESSAGES.sharedKey }}</p>
        </div>
        <p v-if="totals.estimatedCalls > 0" class="mt-3 text-small text-fg-3" data-test="cost-estimated-note">
          {{ COST_MESSAGES.estimatedNote(formatNumber(totals.estimatedCalls)) }}
        </p>
      </template>
      <a v-if="gatewayKeyId && !gateway && gatewayLink" :href="gatewayLink" target="_blank" rel="noopener noreferrer" class="link mt-3 mr-4 inline-block text-small"
         data-test="cost-gateway-link"
      >{{ COSTS.gatewayReport }}</a>
      <RouterLink v-if="costsLink" :to="costsLink" class="link mt-3 inline-block text-small" data-test="cost-link">{{ COSTS.openCosts }}</RouterLink>
    </AsyncState>
  </UiCard>
</template>
