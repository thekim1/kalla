<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { ContentGapReport, ContentGapRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { lastDays } from '@/components/costs/costs'
import { useLoadable } from '@/stores/loadable'
import { formatNumber, quietZero, formatRatio } from '@/utils/format'
import { CONTENT_GAPS as L, COSTS } from '@/utils/labels'
import { CONTENT_GAP_MESSAGES } from '@/utils/messages'

// The Content gaps card of admin design §8.7 (Decisions "Content-gap report") on the assistant's Quality tab: the share of the
// period's questions that ended not found or were answered from general knowledge, and the documents those questions came
// nearest to, most first, with the whole list (at most 200) and its CSV. Counts only: no question, answer or subject.
const props = defineProps<{ assistantId: string }>()

/** The rows shown until Show all (§8.7). */
const SHOWN = 20

const report = useLoadable<ContentGapReport | null>(null)
const days = ref('30')
const showAll = ref(false)
const periodOptions = Object.entries(COSTS.periods).map(([value, label]) => ({ value, label }))
const period = computed(() => lastDays(Number(days.value)))

async function load(): Promise<void> {
  showAll.value = false
  await report.run(() => api.gaps.assistant(props.assistantId, period.value))
}

watch([() => props.assistantId, days], load, { immediate: true })

const gaps = computed(() => (report.data.value ? report.data.value.notFound + report.data.value.answeredGeneral : 0))
const documents = computed(() => report.data.value?.documents ?? [])
const shown = computed(() => (showAll.value ? documents.value : documents.value.slice(0, SHOWN)))
const csvUrl = computed(() => api.gaps.csvUrl(props.assistantId, period.value))
const share = computed(() => {
  const r = report.data.value
  return r ? CONTENT_GAP_MESSAGES.share(formatRatio(r.turns > 0 ? Math.min(1, gaps.value / r.turns) : null), formatNumber(r.notFound), formatNumber(r.answeredGeneral)) : ''
})

const columns: Column[] = [
  { key: 'document', label: L.document },
  { key: 'knowledgeBase', label: L.knowledgeBase },
  { key: 'notFound', label: L.notFound, align: 'right' },
  { key: 'answeredGeneral', label: L.answeredGeneral, align: 'right' },
]

function key(row: ContentGapRow): string {
  return `${row.collectionId}/${row.documentId}`
}
</script>

<template>
  <UiCard :title="L.card" data-test="content-gaps">
    <template #actions>
      <FilterSelect v-model="days" :label="COSTS.period" :options="periodOptions" />
    </template>
    <AsyncState :loading="report.loading.value" :error="report.error.value" :empty="!report.data.value" @retry="load()">
      <p v-if="gaps === 0" class="text-small text-fg-2" data-test="content-gaps-empty">{{ CONTENT_GAP_MESSAGES.empty }}</p>
      <template v-else>
        <p class="mb-4" data-test="content-gaps-share">{{ share }}</p>
        <DataTable :columns="columns" :rows="shown" :row-key="key" :caption="L.caption" data-test="content-gaps-documents">
          <template #cell-document="{ row }">
            <span v-if="row.noHit" class="text-fg-2">{{ L.noHit }}</span>
            <span v-else-if="row.notShared" class="text-fg-3" data-test="content-gaps-not-shared">{{ L.notShared }}</span>
            <span v-else-if="row.deleted" class="text-fg-3">{{ L.deleted }}</span>
            <RouterLink
              v-else-if="row.canOpen" :to="{ name: 'collection', params: { id: row.collectionId }, query: { tab: 'documents' } }" class="break-words text-accent-ink underline"
              data-test="content-gaps-link"
            >{{ row.documentTitle }}</RouterLink>
            <span v-else class="break-words">{{ row.documentTitle }}</span>
          </template>
          <template #cell-knowledgeBase="{ row }">
            <span v-if="row.noHit || row.notShared" class="text-fg-3" aria-hidden="true">–</span>
            <template v-else>{{ row.collectionName ?? L.deleted }}</template>
          </template>
          <template #cell-notFound="{ row }"><span class="tabular" :class="quietZero(row.notFound)">{{ formatNumber(row.notFound) }}</span></template>
          <template #cell-answeredGeneral="{ row }"><span class="tabular" :class="quietZero(row.answeredGeneral)">{{ formatNumber(row.answeredGeneral) }}</span></template>
        </DataTable>
        <div class="mt-3 flex flex-wrap items-center gap-4">
          <UiButton v-if="documents.length > SHOWN" data-test="content-gaps-show-all" @click="showAll = !showAll">
            {{ showAll ? L.showFewer : L.showAll(documents.length) }}
          </UiButton>
          <a :href="csvUrl" class="text-small text-accent-ink underline" download data-test="content-gaps-csv">{{ COSTS.downloadCsv }}</a>
        </div>
      </template>
    </AsyncState>
  </UiCard>
</template>
