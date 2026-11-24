<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { FileSort, FileStateFilter, Source, UploadFile, UploadSummary } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import ToggleChip from '@/components/ui/ToggleChip.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useDebouncedWatch } from '@/composables/useDebouncedWatch'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { FILES } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import FileDetails from './FileDetails.vue'
import FileRows from './FileRows.vue'
import FileUpload from './FileUpload.vue'
import FilesSummary from './FilesSummary.vue'
import { POLL_MS } from './files'

// The Files card of an upload source (admin design §5.5.7, §4.5; connectors §9): adding files and folders (FileUpload),
// the summary bar with live progress, and a list for hundreds of files: search on the path, state chips with the
// summary's counts, sort by name (folder headers) or newest, select all loaded rows, remove the selection, Retry failed,
// Show more; a row opens its details. While anything is processing, the summary and the visible rows refresh every 3 s;
// polling stops when nothing is processing, while the page is hidden or when the card goes away, and starts again after
// an upload, a removal or a retry. Removed files (Show removed) are listed for a platform admin to restore within the
// 14-day grace (connectors §9.3, B-24). The same card is on the source page and on the knowledge base's Content tab.
const props = withDefaults(defineProps<{
  source: Source
  canEdit: boolean
  /** On the knowledge base page: the folder hint links to its Topics tab. */
  topicsLink?: boolean
  /** The assistants that use the knowledge base, shown in a file's details (the collection page has them). */
  usedBy?: string
}>(), { topicsLink: false, usedBy: undefined })
const emit = defineEmits<{ (e: 'committed'): void; (e: 'topics'): void }>()
const ui = useUiStore()
const auth = useAuthStore()

/** Rows per page; a refresh re-reads the rows already shown, at most the API's limit per request. */
const PAGE = 50
const MAX_LIMIT = 200
const SEARCH_DELAY_MS = 300
const STATES: FileStateFilter[] = ['processing', 'indexed', 'failed']

const canRestore = computed(() => auth.can('admin'))
const summary = ref<UploadSummary | null>(null)
const files = ref<UploadFile[]>([])
const next = ref<string | null>(null)
const loaded = ref(false)
const showDeleted = ref(false)
const search = ref('')
const q = ref('')
const state = ref<FileStateFilter | ''>('')
const sort = ref<FileSort>('name')
const selected = ref<string[]>([])
const removing = ref<string[]>([])
const restoring = ref<UploadFile | null>(null)
const details = ref<UploadFile | null>(null)
const detailsOpen = ref(false)
const retrying = ref(false)
const error = ref<unknown>(null)
const uploader = ref<InstanceType<typeof FileUpload> | null>(null)
let request = 0
let pollTimer: ReturnType<typeof setTimeout> | null = null
let active = true

const sortOptions = (['name', 'uploaded'] as const).map((s) => ({ value: s, label: FILES.sorts[s]! }))
const chips = computed(() => [
  { value: '' as const, label: FILES.filterCount(FILES.filters.all!, summary.value?.total ?? 0) },
  ...STATES.map((s) => ({ value: s, label: FILES.filterCount(FILES.filters[s]!, summary.value?.[s] ?? 0) })),
])
const editing = computed(() => props.canEdit && !showDeleted.value)
const allSelected = computed(() => files.value.length > 0 && files.value.every((f) => selected.value.includes(f.id)))

/**
 * The list from the top, the next page (`more`), or the rows already shown again (`refresh`, #709): page by page from
 * the top with `after` until as many rows as are shown are read, so a poll keeps every loaded page, the cursor after
 * them and the selection of rows still listed. The rows keep their ids, so the list patches in place and keeps its scroll.
 */
async function load(mode: 'reset' | 'more' | 'refresh' = 'reset'): Promise<void> {
  const mine = ++request
  const query = {
    deleted: showDeleted.value || undefined, q: q.value || undefined, state: showDeleted.value ? undefined : state.value || undefined, sort: sort.value,
  }
  try {
    if (mode === 'more') {
      const page = await api.sources.files(props.source.id, { ...query, after: next.value ?? undefined, limit: PAGE })
      if (mine !== request) return
      files.value = [...files.value, ...page.items]
      next.value = page.next
      return
    }
    const want = mode === 'refresh' ? Math.max(files.value.length, PAGE) : PAGE
    const rows: UploadFile[] = []
    let after: string | null = null
    do {
      const page = await api.sources.files(props.source.id, { ...query, after: after ?? undefined, limit: Math.min(want - rows.length, MAX_LIMIT) })
      if (mine !== request) return
      rows.push(...page.items)
      after = page.items.length ? page.next : null
    } while (mode === 'refresh' && after && rows.length < want)
    files.value = rows
    next.value = after
    loaded.value = true
    const ids = new Set(rows.map((f) => f.id))
    selected.value = selected.value.filter((id) => ids.has(id))
  } catch (e) {
    if (mine === request) error.value = e
  }
}

async function loadSummary(): Promise<void> {
  summary.value = await api.sources.filesSummary(props.source.id).catch((e) => {
    error.value = e
    return summary.value
  })
}

function stopPolling(): void {
  if (pollTimer) clearTimeout(pollTimer)
  pollTimer = null
}

/** The next refresh in 3 s; `force` after a change that starts processing, before the summary says so. */
function schedule(force = false): void {
  stopPolling()
  if (!active || document.hidden) return
  if (force || (summary.value?.processing ?? 0) > 0) pollTimer = setTimeout(() => void tick(), POLL_MS)
}

async function tick(): Promise<void> {
  pollTimer = null
  await Promise.all([loadSummary(), load('refresh')])
  schedule()
}

function visibilityChanged(): void {
  if (document.hidden) stopPolling()
  else if ((summary.value?.processing ?? 0) > 0) void tick()
}

/** After an upload, a removal, a restore or a retry: the summary and rows now, then polling while anything processes. */
async function changed(): Promise<void> {
  await Promise.all([loadSummary(), load('refresh')])
  schedule(true)
}

async function committed(): Promise<void> {
  emit('committed')
  await changed()
}

function toggleAll(on: boolean): void {
  selected.value = on ? files.value.map((f) => f.id) : []
}

function confirmRemove(ids: string[]): void {
  removing.value = ids
}

async function remove(): Promise<void> {
  const ids = removing.value
  const batch = await api.sources.uploadBatch(props.source.id)
  await api.uploads.commit(batch.batchId, { files: [], deletes: ids })
  ui.notify(FILES.deletedDone(ids.length))
  selected.value = selected.value.filter((id) => !ids.includes(id))
  await committed()
}

async function restore(): Promise<void> {
  const file = restoring.value
  if (!file) return
  await api.sources.restoreFiles(props.source.id, [file.id])
  ui.notify(FILES.restored(file.fileName))
  await committed()
}

async function retryFailed(): Promise<void> {
  retrying.value = true
  error.value = null
  try {
    await api.sources.retryFailed(props.source.id)
    ui.notify(FILES.retryStarted)
    await changed()
  } catch (e) {
    error.value = e
  } finally {
    retrying.value = false
  }
}

function open(file: UploadFile): void {
  details.value = file
  detailsOpen.value = true
}

function replace(file: UploadFile): void {
  detailsOpen.value = false
  uploader.value?.replace(file)
}

function removeOne(file: UploadFile): void {
  detailsOpen.value = false
  confirmRemove([file.id])
}

async function toggleDeleted(): Promise<void> {
  showDeleted.value = !showDeleted.value
  selected.value = []
  await load()
}

useDebouncedWatch(search, (value) => {
  q.value = value.trim()
}, SEARCH_DELAY_MS)
watch([q, state, sort], () => void load())
// A refreshed row replaces the one the details show.
watch(files, (list) => {
  if (details.value) details.value = list.find((f) => f.id === details.value!.id) ?? details.value
})

onMounted(async () => {
  document.addEventListener('visibilitychange', visibilityChanged)
  await Promise.all([loadSummary(), load()])
  schedule()
})
onBeforeUnmount(() => {
  active = false
  stopPolling()
  document.removeEventListener('visibilitychange', visibilityChanged)
})
// The source page's header action Upload files (admin design §5.5.3): the upload area's own file picker. With Show
// removed on, the upload area is hidden, so the list goes back to the current files first (the reload is not awaited,
// so the picker still opens within the click's user activation).
async function upload(): Promise<void> {
  if (showDeleted.value) {
    showDeleted.value = false
    selected.value = []
    void load()
    await nextTick()
  }
  uploader.value?.pick()
}
defineExpose({ upload })
</script>

<template>
  <UiCard :title="FILES.title" data-test="files">
    <FileUpload v-if="editing" ref="uploader" :source="source" :topics-link="topicsLink" class="mb-5" @committed="committed" @topics="emit('topics')" />
    <FilesSummary v-if="summary" :summary="summary" class="mb-4" />
    <InlineError v-if="error" :message="problemMessage(error)" class="mb-3" />

    <div class="mb-3 flex flex-wrap items-center justify-between gap-2">
      <h3 class="font-medium">{{ FILES.list }}</h3>
      <div class="flex flex-wrap gap-2">
        <UiButton
          v-if="editing && (summary?.failed ?? 0) > 0" size="sm" icon="refresh" :loading="retrying" data-test="retry-failed" @click="retryFailed"
        >
          {{ FILES.retryFailed }}
        </UiButton>
        <UiButton v-if="editing && selected.length" variant="danger" size="sm" data-test="delete-files" @click="confirmRemove([...selected])">
          {{ FILES.deleteSelected(selected.length) }}
        </UiButton>
        <UiButton size="sm" variant="quiet" :aria-pressed="showDeleted" data-test="show-deleted" @click="toggleDeleted">{{ FILES.showDeleted }}</UiButton>
      </div>
    </div>
    <div class="mb-3 flex flex-wrap items-end gap-3">
      <div class="min-w-[220px] flex-1">
        <TextField id="files-search" v-model="search" type="search" :label="FILES.search" autocomplete="off" data-test="files-search" />
      </div>
      <FilterSelect :model-value="sort" :label="FILES.sort" :options="sortOptions" data-test="files-sort" @update:model-value="sort = $event as FileSort" />
    </div>
    <div v-if="!showDeleted" role="group" :aria-label="FILES.filterLabel" class="mb-3 flex flex-wrap gap-2" data-test="files-filters">
      <ToggleChip
        v-for="c in chips" :key="c.value" :label="c.label" :model-value="state === c.value" :data-test="`filter-${c.value || 'all'}`"
        @update:model-value="state = c.value"
      />
    </div>
    <CheckField v-if="editing && files.length" :model-value="allSelected" :label="FILES.selectAll" class="mb-2" data-test="select-all" @update:model-value="toggleAll" />

    <p v-if="loaded && files.length === 0" class="text-fg-2" data-test="files-empty">{{ q || state ? FILES.noMatches : FILES.empty }}</p>
    <FileRows
      v-else v-model:selected="selected" :files="files" :grouped="sort === 'name'" :can-edit="canEdit" :show-deleted="showDeleted" :can-restore="canRestore"
      @open="open" @replace="replace" @restore="restoring = $event" @delete="confirmRemove([$event.id])"
    />
    <UiButton v-if="next" class="mt-3" size="sm" data-test="files-more" @click="load('more')">{{ FILES.more }}</UiButton>

    <FileDetails v-model:open="detailsOpen" :file="details" :can-edit="canEdit" :used-by="usedBy" @replace="replace" @delete="removeOne" />
    <ConfirmDialog
      :open="removing.length > 0" :title="FILES.deleteTitle(removing.length)" :consequence="FILES.deleteConsequence"
      :confirm-label="FILES.deleteSelected(removing.length)" danger :action="remove" @update:open="(o: boolean) => { if (!o) removing = [] }"
    />
    <ConfirmDialog
      :open="restoring !== null" :title="FILES.restoreTitle(restoring?.fileName ?? '')" :consequence="FILES.restoreConsequence"
      :confirm-label="FILES.restore" :action="restore" @update:open="(o: boolean) => { if (!o) restoring = null }"
    />
  </UiCard>
</template>
