<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { DocumentSummary, EvaluationQuestion } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import { useDebouncedWatch } from '@/composables/useDebouncedWatch'
import { COMMON, EVALUATION } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Add or change a human test question (admin design §7.1): the question, an optional expected answer, and up to 10
// expected documents picked by title search; none means the question expects "not found". Editing a generated
// question saves it as a human one (a new question; the API's edit-eval-question only changes the human layer). An expected
// document that was deleted (404) is marked so the editor removes it and picks another (processing design §13.3).
const props = defineProps<{ open: boolean; collectionId: string; question: EvaluationQuestion | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved'): void }>()

const MAX_DOCUMENTS = 10
interface Picked { id: string; title: string; deleted?: boolean }

const text = ref('')
const answer = ref('')
const picked = ref<Picked[]>([])
const search = ref('')
const found = ref<DocumentSummary[]>([])
const searched = ref(false)
const textError = ref<string | null>(null)
const error = ref<unknown>(null)
const busy = ref(false)
let searchRequest = 0

const titleOf = (d: { title: string | null; displayName: string | null; url: string | null }, id: string) => d.title ?? d.displayName ?? d.url ?? id

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    text.value = props.question?.question ?? ''
    answer.value = props.question?.expectedAnswer ?? ''
    search.value = ''
    found.value = []
    searched.value = false
    textError.value = null
    error.value = null
    const ids = props.question?.expectedDocumentIds ?? []
    picked.value = ids.map((id) => ({ id, title: id }))
    // Titles for the expected documents (at most 10 requests).
    const details = await Promise.all(ids.map((id) => api.documents.get(id).catch((e: unknown) => (isApiError(e) && e.status === 404 ? 'deleted' : null))))
    picked.value = ids.map((id, i) => {
      const d = details[i]
      return d === 'deleted' ? { id, title: EVALUATION.deletedDocument, deleted: true } : { id, title: d ? titleOf(d, id) : id }
    })
  },
  { immediate: true },
)

useDebouncedWatch(search, (q) => void runSearch(q), 250)

async function runSearch(q: string): Promise<void> {
  const mine = ++searchRequest
  if (q.trim().length < 2) {
    found.value = []
    searched.value = false
    return
  }
  const page = await api.documents.list({ collectionId: props.collectionId, q: q.trim(), limit: 10 }).catch(() => null)
  if (mine !== searchRequest) return
  found.value = page?.items.filter((d) => d.live) ?? []
  searched.value = true
}

function add(d: DocumentSummary): void {
  if (picked.value.length >= MAX_DOCUMENTS || picked.value.some((p) => p.id === d.id)) return
  picked.value = [...picked.value, { id: d.id, title: titleOf(d, d.id) }]
}

const hasDeleted = () => picked.value.some((p) => p.deleted)

function remove(id: string): void {
  picked.value = picked.value.filter((p) => p.id !== id)
}

async function save(): Promise<void> {
  textError.value = text.value.trim() ? null : EVALUATION.questionRequired
  if (textError.value || hasDeleted()) return
  busy.value = true
  error.value = null
  const body = { question: text.value.trim(), expectedAnswer: answer.value.trim() || null, expectedDocumentIds: picked.value.map((p) => p.id) }
  try {
    if (props.question?.layer === 'human') await api.evaluation.updateQuestion(props.collectionId, props.question.id, body)
    else await api.evaluation.addQuestion(props.collectionId, body)
    emit('saved')
    emit('update:open', false)
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog :open="open" :title="question ? EVALUATION.editTitle : EVALUATION.addTitle" wide @update:open="emit('update:open', $event)">
    <p v-if="question && question.layer !== 'human'" class="text-small text-fg-2">{{ EVALUATION.editGenerated }}</p>
    <TextField id="eval-question" v-model="text" :label="EVALUATION.questionLabel" :error="textError" />
    <TextAreaField id="eval-answer" v-model="answer" :label="EVALUATION.expectedAnswer" />

    <fieldset>
      <legend class="mb-1 font-medium">{{ EVALUATION.expectedDocuments }}</legend>
      <p class="mb-2 text-small text-fg-2">{{ EVALUATION.expectedHint }}</p>
      <ul v-if="picked.length" class="mb-3 grid gap-1" data-test="picked">
        <li v-for="p in picked" :key="p.id" class="flex items-center justify-between gap-2 text-small">
          <span class="break-all" :class="{ 'text-warn': p.deleted }" :data-test="p.deleted ? 'deleted-document' : undefined">{{ p.title }}</span>
          <UiButton size="sm" variant="quiet" :aria-label="EVALUATION.removeDocument(p.title)" @click="remove(p.id)">{{ EVALUATION.retire }}</UiButton>
        </li>
      </ul>
      <p v-else class="mb-3 text-small text-fg-3">{{ EVALUATION.expectedNone }}</p>
      <InlineError v-if="hasDeleted()" :message="EVALUATION.removeDeleted" class="mb-3" />
      <TextField id="eval-document-search" v-model="search" :label="EVALUATION.documentSearch" :disabled="picked.length >= MAX_DOCUMENTS" />
      <p v-if="picked.length >= MAX_DOCUMENTS" class="mt-1 text-small text-fg-2">{{ EVALUATION.maxDocuments }}</p>
      <ul v-if="found.length" class="mt-2 grid gap-1" data-test="found">
        <li v-for="d in found" :key="d.id" class="flex items-center justify-between gap-2 text-small">
          <span class="break-all">{{ titleOf(d, d.id) }}<span v-if="d.url" class="block text-fg-3">{{ d.url }}</span></span>
          <UiButton size="sm" :disabled="picked.some((p) => p.id === d.id)" :aria-label="EVALUATION.addDocument(titleOf(d, d.id))" @click="add(d)">
            {{ EVALUATION.addShort }}
          </UiButton>
        </li>
      </ul>
      <p v-else-if="searched" class="mt-2 text-small text-fg-2">{{ EVALUATION.noDocumentsFound }}</p>
    </fieldset>

    <InlineError v-if="error" :message="problemMessage(error)" />
    <template #actions>
      <UiButton @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
      <UiButton variant="primary" :loading="busy" data-test="save-question" @click="save">{{ EVALUATION.save }}</UiButton>
    </template>
  </UiDialog>
</template>
