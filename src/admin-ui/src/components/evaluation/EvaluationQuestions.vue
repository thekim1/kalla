<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { EvaluationQuestion } from '@/api/types'
import { useKeysetList } from '@/composables/useKeysetList'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useUiStore } from '@/stores/ui'
import { EVALUATION } from '@/utils/labels'
import QuestionDialog from './QuestionDialog.vue'

// The questions table (admin design §7.1, keyset): question, layer, expected documents, state. Editors add, change and
// retire human questions; a generated question is changed by saving it as a human one. A stale human question (not
// retired) expects a deleted document (processing design §13.3): the editor re-points it or retires it; `deleted` is
// their count from the overview, with a shortcut to them.
const props = defineProps<{ collectionId: string; canEdit: boolean; deleted?: number }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const ui = useUiStore()
const layer = ref('')
const state = ref('active')
const q = ref('')
const questions = useKeysetList((after?: string) => api.evaluation.questions(props.collectionId, {
  layer: layer.value || undefined, state: state.value || undefined, q: q.value.trim() || undefined, after, limit: 50,
}))
const editing = ref<EvaluationQuestion | null>(null)
const dialogOpen = ref(false)
const retiring = ref<EvaluationQuestion | null>(null)
let timer: ReturnType<typeof setTimeout> | null = null

const columns: Column[] = [
  { key: 'question', label: EVALUATION.question },
  { key: 'layer', label: EVALUATION.layer },
  { key: 'expected', label: EVALUATION.expected },
  { key: 'state', label: EVALUATION.state },
  ...(props.canEdit ? [{ key: 'actions', label: EVALUATION.edit, hideLabel: true }] : []),
]
const layerOptions = [
  { value: '', label: EVALUATION.allLayers },
  { value: 'human', label: EVALUATION.layers.human! },
  { value: 'generated', label: EVALUATION.layers.generated! },
]
const stateOptions = [
  { value: '', label: EVALUATION.allStates },
  ...['active', 'stale', 'retired'].map((s) => ({ value: s, label: EVALUATION.states[s]! })),
]

/** A human question whose expected document was deleted; a generated stale question is retired at its top-up. */
const documentDeleted = (row: EvaluationQuestion) => row.layer === 'human' && row.state === 'stale' && row.retiredAt === null

function showDeleted(): void {
  layer.value = 'human'
  state.value = 'stale'
}

function openEditor(question: EvaluationQuestion | null): void {
  editing.value = question
  dialogOpen.value = true
}

function saved(): void {
  ui.notify(EVALUATION.saved)
  emit('changed')
}

async function retire(): Promise<void> {
  if (!retiring.value) return
  await api.evaluation.retireQuestion(props.collectionId, retiring.value.id)
  ui.notify(EVALUATION.retired)
  emit('changed')
}

watch([layer, state], () => void questions.reload())
watch(q, () => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => void questions.reload(), 300)
})
onMounted(() => questions.reload())
defineExpose({ reload: () => questions.reload() })
</script>

<template>
  <UiCard :title="EVALUATION.questions" data-test="questions">
    <template #actions>
      <UiButton v-if="canEdit" size="sm" icon="add" data-test="add-question" @click="openEditor(null)">{{ EVALUATION.add }}</UiButton>
    </template>
    <div class="mb-4 flex flex-wrap items-end gap-4">
      <TextField id="eval-q" v-model="q" :label="EVALUATION.search" class="min-w-[200px] flex-1" />
      <FilterSelect v-model="layer" :label="EVALUATION.layer" :options="layerOptions" />
      <FilterSelect v-model="state" :label="EVALUATION.state" :options="stateOptions" />
    </div>
    <div
      v-if="deleted && !(layer === 'human' && state === 'stale')" class="mb-4 flex flex-wrap items-center justify-between gap-3 rounded-tile border border-border p-4"
      data-test="deleted-notice"
    >
      <p class="text-small">{{ EVALUATION.deletedNotice(deleted) }}</p>
      <UiButton size="sm" data-test="show-deleted" @click="showDeleted">{{ EVALUATION.showDeleted }}</UiButton>
    </div>
    <AsyncState
      :loading="questions.loading.value && questions.items.value.length === 0" :error="questions.error.value"
      :empty="questions.items.value.length === 0" :empty-text="EVALUATION.noSet" @retry="questions.reload()"
    >
      <DataTable :columns="columns" :rows="questions.items.value" :row-key="(r) => r.id" :caption="EVALUATION.questionsCaption">
        <template #cell-question="{ row }">
          <span class="font-medium">{{ row.question }}</span>
          <span v-if="row.expectedAnswer" class="block text-small text-fg-3">{{ row.expectedAnswer }}</span>
        </template>
        <template #cell-layer="{ row }">{{ EVALUATION.layers[row.layer] ?? row.layer }}</template>
        <template #cell-expected="{ row }">
          <span class="text-fg-2">{{ row.expectedDocumentIds.length ? EVALUATION.expectedCount(row.expectedDocumentIds.length) : EVALUATION.expectedNone }}</span>
        </template>
        <template #cell-state="{ row }">
          <UiBadge :tone="row.state === 'active' ? 'ok' : row.state === 'stale' ? 'warn' : 'neutral'" :label="EVALUATION.states[row.state] ?? row.state" />
          <span v-if="documentDeleted(row)" class="block text-small text-fg-2" data-test="document-deleted">{{ EVALUATION.documentDeleted }}</span>
        </template>
        <template #cell-actions="{ row }">
          <span v-if="row.state !== 'retired'" class="flex justify-end gap-2">
            <UiButton size="sm" data-test="edit-question" @click="openEditor(row)">{{ EVALUATION.edit }}</UiButton>
            <UiButton v-if="row.layer === 'human'" size="sm" variant="danger" data-test="retire-question" @click="retiring = row">{{ EVALUATION.retire }}</UiButton>
          </span>
        </template>
      </DataTable>
      <UiButton v-if="questions.next.value" class="mt-4" :loading="questions.loading.value" @click="questions.more()">{{ EVALUATION.more }}</UiButton>
    </AsyncState>
    <QuestionDialog v-model:open="dialogOpen" :collection-id="collectionId" :question="editing" @saved="saved" />
    <ConfirmDialog
      :open="retiring !== null" :title="EVALUATION.retireTitle" :consequence="EVALUATION.retireConsequence" :confirm-label="EVALUATION.retire" danger
      :action="retire" @update:open="(open) => { if (!open) retiring = null }"
    />
  </UiCard>
</template>
