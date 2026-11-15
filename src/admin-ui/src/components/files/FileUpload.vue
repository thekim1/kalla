<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Source, UploadFile } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useUiStore } from '@/stores/ui'
import { FILES } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { filesFromDrop, filesFromInput, type PickedFile, skipReason, UPLOAD_ACCEPT } from '@/utils/uploads'
import { baseName, folderOf, sizeText } from './files'

// Adding files to an upload source (admin design §4.5, §5.5.7, connectors §9.1–§9.3): choose files or a folder, or drag
// them onto the drop zone; each file keeps its relative path (`HR/Policys/semester.pdf`), is pre-checked for type, size
// and path, and goes into an upload batch (three at a time, as many batches as the API's limits need; per-file state, a
// failed one is retried in the same batch); then one commit per batch. Replace sends new bytes into a slot (same document).
const props = defineProps<{ source: Source; topicsLink: boolean }>()
const emit = defineEmits<{ (e: 'committed'): void; (e: 'topics'): void }>()
const ui = useUiStore()

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
  /** The relative path sent in `X-File-Name`; just the name for a file without a folder. */
  path: string
  state: 'waiting' | 'sending' | 'sent' | 'failed' | 'skipped'
  error: string | null
  replaces?: string
  /** The batch and the file's number in it, given on the first send and kept for a retry. */
  batch?: Batch
  n?: number
}

/** Files sent at the same time; the API takes them in any order within a batch. */
const PARALLEL_SENDS = 3

/**
 * The most rows listed (#712): a folder of thousands is shown as counts and the rows that need a look first, in this order,
 * so a per-file change re-renders a short list instead of every file.
 */
const MAX_ROWS = 50
const ROW_ORDER: readonly Pending['state'][] = ['failed', 'sending', 'skipped', 'waiting', 'sent']

const pending = ref<Pending[]>([])
const batches = ref<Batch[]>([])
const busy = ref(false)
const reading = ref(false)
const dragging = ref(0)
const error = ref<unknown>(null)
const picker = ref<HTMLInputElement | null>(null)
const folderPicker = ref<HTMLInputElement | null>(null)
const replacePicker = ref<HTMLInputElement | null>(null)
const replacing = ref<UploadFile | null>(null)
let nextKey = 1

const maxMb = computed(() => Number(props.source.settings.maxFileSizeMb ?? 50))
/** The pending files by state, in one pass. */
const byState = computed(() => {
  const groups: Record<Pending['state'], Pending[]> = { waiting: [], sending: [], sent: [], failed: [], skipped: [] }
  for (const p of pending.value) groups[p.state].push(p)
  return groups
})
const sendable = computed(() => byState.value.waiting.length + byState.value.failed.length + byState.value.sending.length)
const toCommit = computed(() => pending.value.length - byState.value.skipped.length)
const skipped = computed(() => byState.value.skipped.length)
const sent = computed(() => byState.value.sent.length)
const shown = computed(() => ROW_ORDER.flatMap((state) => byState.value[state]).slice(0, MAX_ROWS))
const hidden = computed(() => pending.value.length - shown.value.length)

/** Adds files to the list; those the API would refuse are listed as skipped with the reason and never sent. */
function add(chosen: PickedFile[]): void {
  const maxBytes = maxMb.value * 1024 * 1024
  pending.value = [...pending.value, ...chosen.map(({ file, path }): Pending => {
    const reason = skipReason(file, maxBytes, path)
    return { key: nextKey++, file, path, state: reason ? 'skipped' : 'waiting', error: reason ? FILES.skipReasons[reason]! : null }
  })]
}

function choose(event: Event): void {
  const input = event.target as HTMLInputElement
  add(filesFromInput(input))
  input.value = ''
}

async function dropped(event: DragEvent): Promise<void> {
  dragging.value = 0
  if (!event.dataTransfer || busy.value) return
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
    await api.uploads.put(p.batch!.id, p.n!, p.file, p.path, p.replaces)
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
    ui.notify(sent.length === 1 && sent[0]!.replaces ? FILES.replacedDone(baseName(sent[0]!.path)) : FILES.committed(sent.length))
    pending.value = pending.value.filter((p) => p.state !== 'skipped')
    batches.value = []
    emit('committed')
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

/** Replace (from a row or the details): pick the new file; it keeps the replaced file's folder. */
function replace(file: UploadFile): void {
  replacing.value = file
  replacePicker.value?.click()
}

async function chooseReplacement(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !replacing.value) return
  const folder = folderOf(replacing.value.fileName)
  const path = folder ? `${folder}/${file.name}` : file.name
  pending.value = [{ key: nextKey++, file, path, state: 'waiting', error: null, replaces: replacing.value.id }]
  batches.value = []
  await upload()
}

defineExpose({ replace })

onMounted(() => {
  // `webkitdirectory` is not in the DOM typings; every current browser supports it on a file input.
  folderPicker.value?.setAttribute('webkitdirectory', '')
})
</script>

<template>
  <div>
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
    <p class="mt-2 text-small text-fg-3" data-test="folder-hint">
      {{ FILES.folderHint }}
      <a v-if="topicsLink" href="?tab=topics" class="link" data-test="topics-link" @click.prevent="emit('topics')">{{ FILES.topicsLink }}</a>
    </p>
    <template v-if="pending.length">
      <div class="mt-3 flex flex-wrap items-center gap-3 text-small" data-test="pending-summary">
        <span class="font-medium">{{ FILES.ready(toCommit) }}</span>
        <span v-if="sent" class="text-fg-3" data-test="sent-count">{{ FILES.sentCount(sent, toCommit) }}</span>
        <span v-if="skipped" class="text-fg-3">{{ FILES.skippedCount(skipped) }}</span>
        <UiButton v-if="skipped" size="sm" variant="quiet" data-test="clear-skipped" @click="clearSkipped">{{ FILES.clearSkipped }}</UiButton>
        <span class="flex-1" />
        <UiButton v-if="sendable" variant="primary" :loading="busy" data-test="upload" @click="upload">{{ FILES.upload(toCommit) }}</UiButton>
      </div>
      <ul class="mt-2 grid max-h-72 gap-1 overflow-y-auto" aria-live="polite" data-test="pending">
        <li v-for="p in shown" :key="p.key" class="flex flex-wrap items-center gap-2 text-small">
          <span class="break-all font-medium" :class="{ 'text-fg-3': p.state === 'skipped' }">{{ p.path }}</span>
          <span class="text-fg-3">{{ sizeText(p.file.size) }}</span>
          <UiBadge :tone="p.state === 'sent' ? 'ok' : p.state === 'failed' ? 'danger' : 'neutral'" :label="FILES.fileStates[p.state]" />
          <span v-if="p.error" :class="p.state === 'skipped' ? 'text-fg-3' : 'text-danger'">{{ p.error }}</span>
          <UiButton v-if="p.state !== 'sending'" size="sm" variant="quiet" :aria-label="FILES.remove" @click="drop(p)">×</UiButton>
        </li>
        <li v-if="hidden" class="text-small text-fg-3" data-test="pending-more">{{ FILES.moreFiles(hidden) }}</li>
      </ul>
    </template>
    <InlineError v-if="error" :message="problemMessage(error)" class="mt-3" />
  </div>
</template>
