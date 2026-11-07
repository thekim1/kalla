<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Source, UploadFile } from '@/api/types'
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

// Filer of an upload source (admin design §4.5, connectors §9.1, §9.3): choose files, send each to one upload batch
// (per-file state, retry a failed one), commit them as one sync; the source's slots with their state; Ersätt sends new
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

interface Pending {
  file: File
  n: number
  state: 'waiting' | 'sending' | 'sent' | 'failed'
  error: string | null
  replaces?: string
}

const files = ref<UploadFile[]>([])
const next = ref<string | null>(null)
const showDeleted = ref(false)
const pending = ref<Pending[]>([])
const batchId = ref<string | null>(null)
const busy = ref(false)
const error = ref<unknown>(null)
const selected = ref<string[]>([])
const deleting = ref(false)
const picker = ref<HTMLInputElement | null>(null)
const replacePicker = ref<HTMLInputElement | null>(null)
const replacing = ref<UploadFile | null>(null)

const maxMb = computed(() => Number(props.source.settings.maxFileSizeMb ?? 50))
const sendable = computed(() => pending.value.filter((p) => p.state !== 'sent').length)
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

function choose(event: Event): void {
  const input = event.target as HTMLInputElement
  const start = pending.value.length
  pending.value = [...pending.value, ...[...(input.files ?? [])].map((file, i) => ({ file, n: start + i + 1, state: 'waiting' as const, error: null }))]
  input.value = ''
}

/** Sends every file not yet sent to the batch (created on first use); a failed file keeps its error and may be retried. */
async function send(): Promise<boolean> {
  batchId.value ??= (await api.sources.uploadBatch(props.source.id)).batchId
  for (const p of pending.value.filter((x) => x.state !== 'sent')) {
    p.state = 'sending'
    p.error = null
    try {
      await api.uploads.put(batchId.value, p.n, p.file, p.replaces)
      p.state = 'sent'
    } catch (e) {
      p.state = 'failed'
      p.error = problemMessage(e)
    }
  }
  return pending.value.every((p) => p.state === 'sent')
}

async function upload(): Promise<void> {
  busy.value = true
  error.value = null
  try {
    if (!(await send())) return
    const sent = pending.value
    await api.uploads.commit(batchId.value!, { files: sent.map((p) => p.n), deletes: [] })
    ui.notify(sent.length === 1 && sent[0]!.replaces ? FILES.replacedDone(sent[0]!.file.name) : FILES.committed(sent.length))
    pending.value = []
    batchId.value = null
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

function startReplace(file: UploadFile): void {
  replacing.value = file
  replacePicker.value?.click()
}

async function chooseReplacement(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !replacing.value) return
  pending.value = [{ file, n: 1, state: 'waiting', error: null, replaces: replacing.value.id }]
  batchId.value = null
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

onMounted(() => load())
</script>

<template>
  <UiCard :title="FILES.title" data-test="files">
    <template v-if="canEdit && !showDeleted">
      <p class="mb-3 text-small text-fg-2">{{ FILES.lead(maxMb) }}</p>
      <input ref="picker" type="file" multiple class="sr-only" :aria-label="FILES.choose" data-test="file-input" @change="choose" />
      <input ref="replacePicker" type="file" class="sr-only" :aria-label="FILES.replace" tabindex="-1" data-test="replace-input" @change="chooseReplacement" />
      <div class="flex flex-wrap gap-2">
        <UiButton icon="upload_file" @click="picker?.click()">{{ FILES.choose }}</UiButton>
        <UiButton v-if="sendable" variant="primary" :loading="busy" data-test="upload" @click="upload">{{ FILES.upload(pending.length) }}</UiButton>
      </div>
      <ul v-if="pending.length" class="mt-3 grid gap-1" aria-live="polite" data-test="pending">
        <li v-for="p in pending" :key="p.n" class="flex flex-wrap items-center gap-2 text-small">
          <span class="font-medium">{{ p.file.name }}</span>
          <span class="text-fg-3">{{ sizeText(p.file.size) }}</span>
          <UiBadge :tone="p.state === 'sent' ? 'ok' : p.state === 'failed' ? 'danger' : 'neutral'" :label="FILES.fileStates[p.state]" />
          <span v-if="p.error" class="text-danger">{{ p.error }}</span>
          <UiButton v-if="p.state !== 'sending'" size="sm" variant="quiet" :aria-label="FILES.remove" @click="drop(p)">×</UiButton>
        </li>
      </ul>
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
