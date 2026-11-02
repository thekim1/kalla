<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { EvaluationQuestion } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { EVALUATION } from '@/utils/labels'
import QuestionDialog from './QuestionDialog.vue'

// The questions table (admin design §7.1, keyset): question, layer, expected documents, state. Editors add, change and
// retire human questions; a generated question is changed by saving it as a human one.
const props = defineProps<{ collectionId: string; canEdit: boolean }>()
const emit = defineEmits<{ (e: 'changed'): void }>()
const ui = useUiStore()
const questions = useLoadable<EvaluationQuestion[]>([])
const next = ref<string | null>(null)
const layer = ref('')
const state = ref('active')
const q = ref('')
const editing = ref<EvaluationQuestion | null>(null)
const dialogOpen = ref(false)
const retiring = ref<EvaluationQuestion | null>(null)
let request = 0
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

async function load(append = false): Promise<void> {
  const mine = ++request
  const previous = append ? questions.data.value : []
  await questions.run(async () => {
    const page = await api.evaluation.questions(props.collectionId, {
      layer: layer.value || undefined, state: state.value || undefined, q: q.value.trim() || undefined, after: append ? next.value ?? undefined : undefined, limit: 50,
    })
    if (mine !== request) return questions.data.value
    next.value = page.next
    return [...previous, ...page.items]
  })
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

watch([layer, state], () => void load())
watch(q, () => {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => void load(), 300)
})
onMounted(() => load())
defineExpose({ reload: () => load() })
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
    <AsyncState
      :loading="questions.loading.value && questions.data.value.length === 0" :error="questions.error.value"
      :empty="questions.data.value.length === 0" :empty-text="EVALUATION.noSet" @retry="load()"
    >
      <DataTable :columns="columns" :rows="questions.data.value" :row-key="(r) => r.id" :caption="EVALUATION.questionsCaption">
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
        </template>
        <template #cell-actions="{ row }">
          <span v-if="row.state !== 'retired'" class="flex justify-end gap-2">
            <UiButton size="sm" data-test="edit-question" @click="openEditor(row)">{{ EVALUATION.edit }}</UiButton>
            <UiButton v-if="row.layer === 'human'" size="sm" variant="danger" data-test="retire-question" @click="retiring = row">{{ EVALUATION.retire }}</UiButton>
          </span>
        </template>
      </DataTable>
      <UiButton v-if="next" class="mt-4" :loading="questions.loading.value" @click="load(true)">{{ EVALUATION.more }}</UiButton>
    </AsyncState>
    <QuestionDialog v-model:open="dialogOpen" :collection-id="collectionId" :question="editing" @saved="saved" />
    <ConfirmDialog
      :open="retiring !== null" :title="EVALUATION.retireTitle" :consequence="EVALUATION.retireConsequence" :confirm-label="EVALUATION.retire" danger
      :action="retire" @update:open="(open) => { if (!open) retiring = null }"
    />
  </UiCard>
</template>
