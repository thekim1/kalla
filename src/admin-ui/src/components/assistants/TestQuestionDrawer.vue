<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { DocumentSummary, TestQuestion } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useDebouncedWatch } from '@/composables/useDebouncedWatch'
import { useFormErrors } from '@/composables/useFormErrors'
import { ASSISTANT_TESTS as L, COMMON } from '@/utils/labels'

// A test question (admin design §7.4, V33): the question, what a good answer does, and optionally a document the answer
// should cite (picked by title in the knowledge bases the assistant links) and a phrase it should contain.
const props = defineProps<{
  open: boolean
  assistantId: string
  question: TestQuestion | null
  /** A question to start from (Save as test question in the test panel). */
  initialQuestion?: string
  collections: { id: string; name: string }[]
}>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', question: TestQuestion): void }>()

const text = ref('')
const behaviour = ref('')
const phrase = ref('')
const document = ref<{ id: string; title: string } | null>(null)
const collectionId = ref('')
const search = ref('')
const found = ref<DocumentSummary[]>([])
const saving = ref(false)
const form = useFormErrors({ question: 'test-question', expectedBehaviour: 'test-question-behaviour', expectedDocumentId: 'test-question-search', expectedPhrase: 'test-question-phrase' })
const collectionOptions = computed(() => props.collections.map((c) => ({ value: c.id, label: c.name })))
let searchRequest = 0

const titleOf = (d: { title: string | null; displayName: string | null; url: string | null }, id: string) => d.title ?? d.displayName ?? d.url ?? id

watch(
  () => props.open,
  (open) => {
    if (!open) return
    text.value = props.question?.question ?? props.initialQuestion ?? ''
    behaviour.value = props.question?.expectedBehaviour ?? ''
    phrase.value = props.question?.expectedPhrase ?? ''
    const expected = props.question?.expectedDocument
    document.value = expected ? { id: expected.id, title: expected.title ?? expected.id } : null
    collectionId.value = props.collections[0]?.id ?? ''
    search.value = ''
    found.value = []
    form.clear()
  },
)

// The document search once the field and the knowledge base stand still for 250 ms (useDebouncedWatch, #980).
useDebouncedWatch(() => [search.value, collectionId.value], () => void runSearch(), 250)

async function runSearch(): Promise<void> {
  const mine = ++searchRequest
  const q = search.value.trim()
  if (q.length < 2 || !collectionId.value) {
    found.value = []
    return
  }
  const page = await api.documents.list({ collectionId: collectionId.value, q, limit: 10 }).catch(() => null)
  if (mine !== searchRequest) return
  found.value = page?.items.filter((d) => d.live) ?? []
}

async function save(): Promise<void> {
  if (!form.validate({ question: !text.value.trim() && L.questionRequired, expectedBehaviour: !behaviour.value.trim() && L.behaviourRequired })) return
  saving.value = true
  try {
    const body = {
      question: text.value.trim(), expectedBehaviour: behaviour.value.trim(), expectedDocumentId: document.value?.id ?? null,
      expectedPhrase: phrase.value.trim() || null,
    }
    const saved = props.question
      ? await api.assistantTests.update(props.assistantId, props.question.id, { ...body, version: props.question.version })
      : await api.assistantTests.create(props.assistantId, body)
    emit('saved', saved)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="question ? L.edit : L.add" wide @update:open="emit('update:open', $event)">
    <form id="test-question-form" class="grid gap-5" novalidate @submit.prevent="save">
      <TextAreaField id="test-question" v-model="text" :label="L.question" :rows="2" :error="form.errors.question" />
      <TextAreaField
        id="test-question-behaviour" v-model="behaviour" :label="L.expectedBehaviour" :hint="L.expectedBehaviourHint" :rows="3"
        :error="form.errors.expectedBehaviour"
      />
      <fieldset class="grid gap-3">
        <legend class="mb-1 font-medium">{{ L.expectedDocument }}</legend>
        <p class="text-small text-fg-2">{{ L.expectedDocumentHint }}</p>
        <div v-if="document" class="flex items-center gap-3" data-test="picked-document">
          <span class="font-medium">{{ document.title }}</span>
          <UiButton size="sm" variant="quiet" icon="close" @click="document = null">{{ L.removeDocument }}</UiButton>
        </div>
        <template v-else>
          <SelectField id="test-question-collection" v-model="collectionId" :label="L.knowledgeBase" :options="collectionOptions" />
          <TextField id="test-question-search" v-model="search" :label="L.searchDocument" autocomplete="off" :error="form.errors.expectedDocumentId" />
          <ul v-if="found.length" class="grid gap-1" data-test="found-documents">
            <li v-for="d in found" :key="d.id" class="flex items-center justify-between gap-3">
              <span>{{ titleOf(d, d.id) }}</span>
              <UiButton size="sm" :aria-label="L.pick(titleOf(d, d.id))" @click="document = { id: d.id, title: titleOf(d, d.id) }">{{ L.choose }}</UiButton>
            </li>
          </ul>
        </template>
      </fieldset>
      <TextField
        id="test-question-phrase" v-model="phrase" :label="L.expectedPhrase" :hint="L.expectedPhraseHint" autocomplete="off" :error="form.errors.expectedPhrase"
      />
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="test-question-form" :loading="saving" data-test="save-test-question">{{ L.save }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>
