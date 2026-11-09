<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { DocumentChunk, UploadFile } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { formatDateTime } from '@/utils/format'
import { FILES } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import { extensionOf } from '@/utils/uploads'
import { baseName, folderOf, sizeText, stateTone } from './files'

// A file's details (admin design §5.5.7): name and folder, size, type, uploaded by and when, replaced when, state and
// reason; for an indexed file its document's passages (the count from `/api/documents/{id}`, the first few from
// `/chunks`, which needs Edit), loaded when the panel opens; the assistants that use the knowledge base when the page
// has them (`usedBy`, the collection page's list). Replace and Remove for an editor.
const props = defineProps<{ open: boolean; file: UploadFile | null; canEdit: boolean; usedBy?: string }>()
const emit = defineEmits<{
  (e: 'update:open', value: boolean): void
  (e: 'replace', file: UploadFile): void
  (e: 'delete', file: UploadFile): void
}>()

/** Passages shown with their text. */
const FIRST_CHUNKS = 3

const chunkCount = ref<number | null>(null)
const chunks = ref<DocumentChunk[]>([])
const loading = ref(false)
const error = ref<unknown>(null)
let request = 0

const folder = computed(() => (props.file ? folderOf(props.file.fileName) : ''))
const type = computed(() => (props.file ? extensionOf(baseName(props.file.fileName)).slice(1).toUpperCase() : ''))
const editable = computed(() => props.canEdit && props.file !== null && props.file.deletedAt === null)

async function loadDocument(documentId: string): Promise<void> {
  const mine = ++request
  loading.value = true
  error.value = null
  try {
    const document = await api.documents.get(documentId)
    const live = document.chunkSets.find((s) => s.id === document.liveChunkSetId)
    const first = props.canEdit ? (await api.documents.chunks(documentId)).chunks.slice(0, FIRST_CHUNKS) : []
    if (mine !== request) return
    chunkCount.value = live?.chunkCount ?? 0
    chunks.value = first
  } catch (e) {
    if (mine === request) error.value = e
  } finally {
    if (mine === request) loading.value = false
  }
}

watch(
  // A refresh that indexes the open file (its state changes) loads its passages too.
  () => [props.open, props.file?.id, props.file?.state] as const,
  ([open]) => {
    request++
    chunkCount.value = null
    chunks.value = []
    error.value = null
    loading.value = false
    if (open && props.file?.documentId && props.file.state === 'indexed') void loadDocument(props.file.documentId)
  },
  { immediate: true },
)
</script>

<template>
  <UiDrawer :open="open && file !== null" :title="file ? baseName(file.fileName) : ''" @update:open="emit('update:open', $event)">
    <template v-if="file">
      <dl class="m-0" data-test="file-details">
        <DetailRow :label="FILES.details.folder">
          <span v-if="folder" class="break-all font-mono text-small" data-test="detail-folder">{{ folder }}</span>
          <span v-else class="text-fg-3">{{ FILES.details.noFolder }}</span>
        </DetailRow>
        <DetailRow :label="FILES.details.size"><span class="tabular">{{ sizeText(file.bytes) }}</span></DetailRow>
        <DetailRow v-if="type" :label="FILES.details.type">{{ type }}</DetailRow>
        <DetailRow :label="FILES.details.uploaded">
          <span data-test="detail-uploaded">{{ formatDateTime(file.uploadedAt) }}</span>
        </DetailRow>
        <DetailRow v-if="file.replacedAt" :label="FILES.details.replaced">{{ formatDateTime(file.replacedAt) }}</DetailRow>
        <DetailRow v-if="file.deletedAt" :label="FILES.deletedAt">{{ formatDateTime(file.deletedAt) }}</DetailRow>
        <DetailRow :label="FILES.details.state">
          <span class="grid gap-1">
            <UiBadge :tone="stateTone(file.state)" :label="FILES.states[file.state] ?? file.state" />
            <span v-if="file.reason" class="text-small text-danger" data-test="detail-reason">{{ jobMessage(file.reason) }}</span>
          </span>
        </DetailRow>
        <DetailRow v-if="usedBy !== undefined" :label="FILES.details.usedBy">
          <span v-if="usedBy" data-test="detail-used-by">{{ usedBy }}</span>
          <span v-else class="text-fg-3">{{ FILES.details.notUsed }}</span>
        </DetailRow>
      </dl>

      <section v-if="file.documentId && file.state === 'indexed'" class="mt-5" :aria-label="FILES.details.chunks" aria-live="polite" data-test="detail-chunks">
        <h3 class="mb-2 font-medium">{{ FILES.details.chunks }}</h3>
        <p v-if="loading" class="text-small text-fg-2">{{ FILES.details.loadingChunks }}</p>
        <InlineError v-else-if="error" :message="problemMessage(error)" />
        <template v-else-if="chunkCount !== null">
          <p class="text-small text-fg-2" data-test="chunk-count">
            {{ chunkCount === 0 ? FILES.details.noChunks : FILES.details.chunkCount(chunkCount) }}<template v-if="chunks.length"> · {{ FILES.details.firstChunks(chunks.length) }}</template>
          </p>
          <ol v-if="chunks.length" class="mt-2 grid gap-2">
            <li v-for="c in chunks" :key="c.id" class="rounded-card bg-sunken p-3 text-small" lang="sv" data-test="chunk">
              <span v-if="c.headingPath" class="mb-1 block font-medium">{{ c.headingPath }}</span>
              <span class="line-clamp-4 whitespace-pre-line">{{ c.text }}</span>
            </li>
          </ol>
        </template>
      </section>
    </template>
    <template v-if="file && editable" #footer>
      <UiButton icon="upload_file" data-test="detail-replace" @click="emit('replace', file)">{{ FILES.replace }}</UiButton>
      <UiButton variant="danger" icon="delete" data-test="detail-delete" @click="emit('delete', file)">{{ FILES.details.delete }}</UiButton>
    </template>
  </UiDrawer>
</template>
