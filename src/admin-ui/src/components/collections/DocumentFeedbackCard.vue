<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { DocumentFeedbackReport, DocumentFeedbackRow } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { lastDays } from '@/components/costs/costs'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { formatNumber } from '@/utils/format'
import { COSTS, DOCUMENT_FEEDBACK as L } from '@/utils/labels'
import { DOCUMENT_FEEDBACK_MESSAGES } from '@/utils/messages'

// The Feedback on documents card of admin design §8.8 (Decisions "Downvotes per document") on the knowledge base's Overview,
// for its owning department: the documents most often cited in downvoted answers, over every assistant that cites them, with
// the whole list (at most 200) and its CSV. Counts only: no question, answer or comment, and no assistant's name.
const props = defineProps<{ collectionId: string }>()
const auth = useAuthStore()

/** The rows shown until Show all (§8.8). */
const SHOWN = 10

const report = useLoadable<DocumentFeedbackReport | null>(null)
const days = ref('30')
const showAll = ref(false)
const periodOptions = Object.entries(COSTS.periods).map(([value, label]) => ({ value, label }))
const period = computed(() => lastDays(Number(days.value)))
/** Show the ratings opens the Feedback page, whose reads are the Feedback policy's (§3.3). */
const canShowRatings = computed(() => auth.can('feedback'))
/**
 * Show the ratings opens Feedback on this document over the card's own period (#1051): the last 30 days is Feedback's
 * preset; 90 days or 12 months go as a custom range of the same days.
 */
const ratingsTo = (documentId: string) => ({
  name: 'feedback',
  query: days.value === '30' ? { documentId } : { documentId, period: 'custom', from: period.value.from, to: period.value.to },
})

async function load(): Promise<void> {
  showAll.value = false
  await report.run(() => api.documentFeedback.collection(props.collectionId, period.value))
}

watch([() => props.collectionId, days], load, { immediate: true })

const documents = computed(() => report.data.value?.documents ?? [])
const shown = computed(() => (showAll.value ? documents.value : documents.value.slice(0, SHOWN)))
const csvUrl = computed(() => api.documentFeedback.csvUrl(props.collectionId, period.value))

const columns: Column[] = [
  { key: 'document', label: L.document },
  { key: 'thumbsDown', label: L.thumbsDown, align: 'right' },
  { key: 'thumbsUp', label: L.thumbsUp, align: 'right' },
]

function key(row: DocumentFeedbackRow): string {
  return row.documentId
}
</script>

<template>
  <UiCard :title="L.card" data-test="document-feedback">
    <template #actions>
      <FilterSelect v-model="days" :label="COSTS.period" :options="periodOptions" />
    </template>
    <p class="mb-4 text-small text-fg-2" data-test="document-feedback-explanation">{{ DOCUMENT_FEEDBACK_MESSAGES.explanation }}</p>
    <AsyncState :loading="report.loading.value" :error="report.error.value" :empty="!report.data.value" @retry="load()">
      <p v-if="documents.length === 0" class="text-small text-fg-2" data-test="document-feedback-empty">{{ DOCUMENT_FEEDBACK_MESSAGES.empty }}</p>
      <template v-else>
        <DataTable :columns="columns" :rows="shown" :row-key="key" :caption="L.caption" data-test="document-feedback-documents">
          <template #cell-document="{ row }">
            <span v-if="row.deleted" class="text-fg-3">{{ L.deleted }}</span>
            <RouterLink
              v-else :to="{ name: 'collection', params: { id: collectionId }, query: { tab: 'documents' } }" class="break-words text-accent-ink underline"
              data-test="document-feedback-link"
            >{{ row.title }}</RouterLink>
            <RouterLink
              v-if="canShowRatings" :to="ratingsTo(row.documentId)" class="block text-small text-accent-ink underline"
              data-test="document-feedback-ratings"
            >{{ L.showRatings }}</RouterLink>
          </template>
          <template #cell-thumbsDown="{ row }"><span class="tabular">{{ formatNumber(row.thumbsDown) }}</span></template>
          <template #cell-thumbsUp="{ row }"><span class="tabular">{{ formatNumber(row.thumbsUp) }}</span></template>
        </DataTable>
        <div class="mt-3 flex flex-wrap items-center gap-4">
          <UiButton v-if="documents.length > SHOWN" data-test="document-feedback-show-all" @click="showAll = !showAll">
            {{ showAll ? L.showFewer : L.showAll(documents.length) }}
          </UiButton>
          <a :href="csvUrl" class="text-small text-accent-ink underline" download data-test="document-feedback-csv">{{ COSTS.downloadCsv }}</a>
        </div>
      </template>
    </AsyncState>
  </UiCard>
</template>
