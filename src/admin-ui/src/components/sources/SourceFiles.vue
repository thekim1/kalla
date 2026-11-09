<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Source, UploadFile } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import CheckField from '@/components/ui/CheckField.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatDecimal } from '@/utils/format'
import { FILES } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import { filesFromDrop, filesFromInput, skipReason, UPLOAD_ACCEPT } from '@/utils/uploads'

// Filer of an upload source (admin design §4.5, connectors §9.1, §9.3): choose files, send each to one upload batch
// (per-file state, retry a failed one), commit them as one sync; files and whole folders may also be dragged onto the card
// or picked as a folder, are pre-checked for type and size, and go out in as many batches as the API's limits need; the source's slots with their state; Ersätt sends new
// bytes into a slot (same document); selected slots are deleted through a batch with deletes only. A platform admin
// restores a removed file within the 14-day grace (connectors §9.3, B-24): a batch that re-adds the slot.
const props = defineProps<{ source: Source; canEdit: boolean }>()
const emit = defineEmits<{ (e: 'committed'): void }>()
const ui = useUiStore()
const auth = useAuthStore()
/** The grace of a removed upload (connectors §9.3, §13.4); the API checks it again. */
const RESTORE_GRACE_MS = 14 * 24 * 60 * 60 * 1000
const canRestore = computed(() => auth.hasAnyRole(['rag-platform-admin']))
const restoring = ref<UploadFile | null>(null)
const restorable = (f: UploadFile) => f.deletedAt !== null && Date.now() - Date.parse(f.deletedAt) < RESTORE_GRACE_MS

interface Batch {
  id: string
  count: number
  bytes: number
  maxFiles: number
  maxBytes: number
}

interface Pending {
  key: number
  file: File
  state: 'waiting' | 'sending' | 'sent' | 'failed' | 'skipped'
  error: string | null
  replaces?: string
  /** The batch and the file's number in it, given on the first send and kept for a retry. */
  batch?: Batch
  n?: number
}

/** Files sent at the same time; the API takes them in any order within a batch. */
const PARALLEL_SENDS = 3

const files = ref<UploadFile[]>([])
const next = ref<string | null>(null)
const showDeleted = ref(false)
const pending = ref<Pending[]>([])
const batches = ref<Batch[]>([])
const busy = ref(false)
const reading = ref(false)
const dragging = ref(0)
const error = ref<unknown>(null)
const selected = ref<string[]>([])
const deleting = ref(false)
const picker = ref<HTMLInputElement | null>(null)
const folderPicker = ref<HTMLInputElement | null>(null)
const replacePicker = ref<HTMLInputElement | null>(null)
const replacing = ref<UploadFile | null>(null)
let nextKey = 1

const maxMb = computed(() => Number(props.source.settings.maxFileSizeMb ?? 50))
const sendable = computed(() => pending.value.filter((p) => p.state === 'waiting' || p.state === 'failed' || p.state === 'sending').length)
const toCommit = computed(() => pending.value.filter((p) => p.state !== 'skipped').length)
const skipped = computed(() => pending.value.filter((p) => p.state === 'skipped').length)
const sizeText = (bytes: number) => (bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} kB` : `${formatDecimal(bytes / 1024 / 1024, 1)} MB`)
const stateTone = (state: string) => (state === 'indexed' ? 'ok' : state === 'malware' || state === 'refused' ? 'danger' : 'neutral')

async function load(append = false): Promise<void> {
  const page = await api.sources.files(props.source.id, { deleted: showDeleted.value || undefined, after: append ? next.value ?? undefined : undefined, limit: 50 })
    .catch((e) => {
      error.value = e
      return null
    })
  if (!page) return
  files.value = append ? [...files.value, ...page.items] : page.items
  next.value = page.next
}

/** Adds files to the list; those the API would refuse are listed as skipped with the reason and never sent. */
function add(chosen: File[]): void {
  const maxBytes = maxMb.value * 1024 * 1024
  pending.value = [...pending.value, ...chosen.map((file): Pending => {
    const reason = skipReason(file, maxBytes)
    return { key: nextKey++, file, state: reason ? 'skipped' : 'waiting', error: reason ? FILES.skipReasons[reason]! : null }
  })]
}

function choose(event: Event): void {
  const input = event.target as HTMLInputElement
  add(filesFromInput(input))
  input.value = ''
}

async function dropped(event: DragEvent): Promise<void> {
  dragging.value = 0
  if (!props.canEdit || !event.dataTransfer || busy.value) return
  reading.value = true
  try {
    add(await filesFromDrop(event.dataTransfer))
  } catch (e) {
    error.value = e
  } finally {
    reading.value = false
  }
}

/** The open batch if the file fits its limits, else a new one. */
async function batchFor(file: File): Promise<Batch> {
  const open = batches.value.at(-1)
  if (open && open.count < open.maxFiles && open.bytes + file.size <= open.maxBytes) return open
  const created = await api.sources.uploadBatch(props.source.id)
  const batch: Batch = { id: created.batchId, count: 0, bytes: 0, maxFiles: created.maxFiles, maxBytes: created.maxBytes }
  batches.value = [...batches.value, batch]
  return batches.value.at(-1)!
}

async function sendOne(p: Pending): Promise<void> {
  p.state = 'sending'
  p.error = null
  try {
    await api.uploads.put(p.batch!.id, p.n!, p.file, p.replaces)
    p.state = 'sent'
  } catch (e) {
    p.state = 'failed'
    p.error = problemMessage(e)
  }
}

/** Sends every file not yet sent (batches created as needed); a failed file keeps its error and may be retried. */
async function send(): Promise<boolean> {
  const queue = pending.value.filter((x) => x.state === 'waiting' || x.state === 'failed')
  for (const p of queue) {
    if (p.batch) continue
    const batch = await batchFor(p.file)
    batch.count += 1
    batch.bytes += p.file.size
    p.batch = batch
    p.n = batch.count
  }
  let at = 0
  const worker = async () => {
    while (at < queue.length) await sendOne(queue[at++]!)
  }
  await Promise.all(Array.from({ length: Math.min(PARALLEL_SENDS, queue.length) }, worker))
  return pending.value.every((p) => p.state === 'sent' || p.state === 'skipped')
}

async function upload(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    if (!(await send())) return
    const sent = pending.value.filter((p) => p.state === 'sent')
    // One commit (and one sync) per batch; a committed batch leaves the list at once, so a later failure retries only the rest.
    for (const batch of batches.value) {
      const ofBatch = sent.filter((p) => p.batch === batch)
      if (ofBatch.length) await api.uploads.commit(batch.id, { files: ofBatch.map((p) => p.n!), deletes: [] })
      pending.value = pending.value.filter((p) => p.batch !== batch)
      batches.value = batches.value.filter((b) => b !== batch)
    }
    ui.notify(sent.length === 1 && sent[0]!.replaces ? FILES.replacedDone(sent[0]!.file.name) : FILES.committed(sent.length))
    pending.value = pending.value.filter((p) => p.state !== 'skipped')
    batches.value = []
    emit('committed')
    await load()
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}

function drop(p: Pending): void {
  // A sent file stays in the batch but is left out of the commit; the batch is swept after 24 h.
  pending.value = pending.value.filter((x) => x !== p)
}

function clearSkipped(): void {
  pending.value = pending.value.filter((p) => p.state !== 'skipped')
}

function startReplace(file: UploadFile): void {
  replacing.value = file
  replacePicker.value?.click()
}

async function chooseReplacement(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !replacing.value) return
  pending.value = [{ key: nextKey++, file, state: 'waiting', error: null, replaces: replacing.value.id }]
  batches.value = []
  await upload()
}

async function deleteSelected(): Promise<void> {
  const batch = await api.sources.uploadBatch(props.source.id)
  await api.uploads.commit(batch.batchId, { files: [], deletes: selected.value })
  ui.notify(FILES.deletedDone(selected.value.length))
  selected.value = []
  emit('committed')
  await load()
}

async function restore(): Promise<void> {
  const file = restoring.value
  if (!file) return
  await api.sources.restoreFiles(props.source.id, [file.id])
  ui.notify(FILES.restored(file.fileName))
  emit('committed')
  await load()
}

async function toggleDeleted(): Promise<void> {
  showDeleted.value = !showDeleted.value
  selected.value = []
  await load()
}

onMounted(() => {
  // `webkitdirectory` is not in the DOM typings; every current browser supports it on a file input.
  folderPicker.value?.setAttribute('webkitdirectory', '')
  void load()
})
</script>

<template>
  <UiCard :title="FILES.title" data-test="files">
    <template v-if="canEdit && !showDeleted">
      <p class="mb-3 text-small text-fg-2">{{ FILES.lead(maxMb) }}</p>
      <input ref="picker" type="file" multiple :accept="UPLOAD_ACCEPT" class="sr-only" :aria-label="FILES.choose" tabindex="-1" data-test="file-input" @change="choose" />
      <input ref="folderPicker" type="file" multiple class="sr-only" :aria-label="FILES.chooseFolder" tabindex="-1" data-test="folder-input" @change="choose" />
      <input ref="replacePicker" type="file" class="sr-only" :aria-label="FILES.replace" tabindex="-1" data-test="replace-input" @change="chooseReplacement" />
      <!-- Dropping is a pointer shortcut only: the two buttons inside do the same from the keyboard. -->
      <!-- eslint-disable-next-line vuejs-accessibility/no-static-element-interactions -->
      <section
        :aria-label="FILES.dropHere"
        class="flex flex-col items-center gap-3 rounded-lg border-2 border-dashed px-4 py-6 text-center transition-colors"
        :class="dragging ? 'border-accent bg-accent-soft' : 'border-border'"
        data-test="drop-zone"
        @dragenter.prevent="dragging++"
        @dragover.prevent
        @dragleave="dragging = Math.max(0, dragging - 1)"
        @drop.prevent="dropped"
      >
        <AppIcon name="upload" :size="32" class="text-fg-3" />
        <span class="font-medium">{{ reading ? FILES.reading : dragging ? FILES.dropRelease : FILES.dropHere }}</span>
        <div class="flex flex-wrap justify-center gap-2">
          <UiButton icon="upload_file" :disabled="busy" @click="picker?.click()">{{ FILES.choose }}</UiButton>
          <UiButton icon="folder_open" :disabled="busy" data-test="choose-folder" @click="folderPicker?.click()">{{ FILES.chooseFolder }}</UiButton>
        </div>
      </section>
      <template v-if="pending.length">
        <div class="mt-3 flex flex-wrap items-center gap-3 text-small" data-test="pending-summary">
          <span class="font-medium">{{ FILES.ready(toCommit) }}</span>
          <span v-if="skipped" class="text-fg-3">{{ FILES.skippedCount(skipped) }}</span>
          <UiButton v-if="skipped" size="sm" variant="quiet" data-test="clear-skipped" @click="clearSkipped">{{ FILES.clearSkipped }}</UiButton>
          <span class="flex-1" />
          <UiButton v-if="sendable" variant="primary" :loading="busy" data-test="upload" @click="upload">{{ FILES.upload(toCommit) }}</UiButton>
        </div>
        <ul class="mt-2 grid max-h-72 gap-1 overflow-y-auto" aria-live="polite" data-test="pending">
          <li v-for="p in pending" :key="p.key" class="flex flex-wrap items-center gap-2 text-small">
            <span class="break-all font-medium" :class="{ 'text-fg-3': p.state === 'skipped' }">{{ p.file.name }}</span>
            <span class="text-fg-3">{{ sizeText(p.file.size) }}</span>
            <UiBadge :tone="p.state === 'sent' ? 'ok' : p.state === 'failed' ? 'danger' : 'neutral'" :label="FILES.fileStates[p.state]" />
            <span v-if="p.error" :class="p.state === 'skipped' ? 'text-fg-3' : 'text-danger'">{{ p.error }}</span>
            <UiButton v-if="p.state !== 'sending'" size="sm" variant="quiet" :aria-label="FILES.remove" @click="drop(p)">×</UiButton>
          </li>
        </ul>
      </template>
    </template>
    <InlineError v-if="error" :message="problemMessage(error)" class="mt-3" />

    <div class="mb-2 mt-5 flex flex-wrap items-center justify-between gap-2">
      <h3 class="font-medium">{{ FILES.list }}</h3>
      <div class="flex gap-2">
        <UiButton v-if="canEdit && selected.length && !showDeleted" variant="danger" size="sm" data-test="delete-files" @click="deleting = true">
          {{ FILES.deleteSelected(selected.length) }}
        </UiButton>
        <UiButton size="sm" variant="quiet" :aria-pressed="showDeleted" data-test="show-deleted" @click="toggleDeleted">{{ FILES.showDeleted }}</UiButton>
      </div>
    </div>
    <p v-if="files.length === 0" class="text-fg-2">{{ FILES.empty }}</p>
    <ul v-else class="grid gap-2" data-test="file-list">
      <li v-for="f in files" :key="f.id" class="flex flex-wrap items-center gap-3 border-b border-border pb-2">
        <CheckField
          v-if="canEdit && !showDeleted" :model-value="selected.includes(f.id)" :label="FILES.select(f.fileName)" class="sr-label"
          @update:model-value="selected = $event ? [...selected, f.id] : selected.filter((x) => x !== f.id)"
        />
        <span class="min-w-0 flex-1">
          <span class="block break-all font-medium">{{ f.fileName }}</span>
          <span class="block text-small text-fg-3">
            {{ sizeText(f.bytes) }} · {{ showDeleted ? FILES.deletedAt : FILES.uploaded }} {{ formatDateTime(f.deletedAt ?? f.uploadedAt) }}<template v-if="f.uploadedBy"> · {{ f.uploadedBy }}</template>
          </span>
          <span v-if="f.reason" class="block text-small text-danger">{{ jobMessage(f.reason) }}</span>
        </span>
        <UiBadge :tone="stateTone(f.state)" :label="FILES.states[f.state] ?? f.state" />
        <UiButton v-if="canEdit && !showDeleted" size="sm" :aria-label="FILES.replaceLabel(f.fileName)" :data-test="`replace-${f.id}`" @click="startReplace(f)">
          {{ FILES.replace }}
        </UiButton>
        <template v-if="showDeleted && canRestore">
          <UiButton v-if="restorable(f)" size="sm" :aria-label="FILES.restoreLabel(f.fileName)" :data-test="`restore-${f.id}`" @click="restoring = f">
            {{ FILES.restore }}
          </UiButton>
          <span v-else class="text-small text-fg-3" :data-test="`restore-expired-${f.id}`">{{ FILES.restoreExpired }}</span>
        </template>
      </li>
    </ul>
    <UiButton v-if="next" class="mt-3" size="sm" @click="load(true)">{{ FILES.more }}</UiButton>

    <ConfirmDialog
      v-model:open="deleting" :title="FILES.deleteTitle(selected.length)" :consequence="FILES.deleteConsequence" :confirm-label="FILES.deleteSelected(selected.length)"
      danger :action="deleteSelected"
    />
    <ConfirmDialog
      :open="restoring !== null" :title="FILES.restoreTitle(restoring?.fileName ?? '')" :consequence="FILES.restoreConsequence"
      :confirm-label="FILES.restore" :action="restore" @update:open="(open: boolean) => { if (!open) restoring = null }"
    />
  </UiCard>
</template>
