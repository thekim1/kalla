<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { PreviewChunks, PreviewPii } from '@/api/types'
import { useJobPoll } from '@/composables/useJobPoll'
import InlineError from '@/components/ui/InlineError.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useUiStore } from '@/stores/ui'
import { COMMON, PII_ACTIONS as L, PREVIEW } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// A flagged document's personal-data actions (admin design §4.5, §5.5.2, §5.5.3, V26; B-63), for Manage holders of the
// source's department: Show highlights (the pii-preview job's spans over the chunks' text, the wait while it runs) and Clear
// with a reason of at least 10 characters (the held set is released, later sets covered while no count rises, processing §9.5).
const props = defineProps<{ documentId: string; title: string }>()
const emit = defineEmits<{ (e: 'cleared'): void }>()
const ui = useUiStore()

/** V26: a clearance's reason, trimmed, has at least this many characters. */
const MIN_REASON = 10

const highlightsOpen = ref(false)
const highlights = useJobPoll<unknown>()
const startError = ref<unknown>(null)
const chunks = ref<PreviewChunks['chunks']>([])
const spans = ref<NonNullable<PreviewPii['spans']>>([])
const outputError = ref(false)

const clearOpen = ref(false)
const reason = ref('')
const reasonError = ref<string | null>(null)
const clearError = ref<unknown>(null)
/** Our own text about the clearance's outcome (its job failed, or its state could not be read). */
const clearNotice = ref<string | null>(null)
const clearing = useJobPoll<unknown>()
const sending = ref(false)

const highlightsState = computed(() => {
  // A failed status read ends the poll with `error` set and no job: failed, never "still finding" (#963 review).
  if (startError.value || outputError.value || highlights.error.value
    || (highlights.job.value && highlights.job.value.state !== 'succeeded' && !highlights.polling.value)) return 'failed'
  if (highlights.polling.value || !highlights.job.value) return 'running'
  return 'done'
})

/** The chunks with a match, each cut into plain and marked pieces in text order. */
const marked = computed(() => chunks.value
  .map((c) => {
    const own = spans.value.filter((s) => s.ordinal === c.ordinal).sort((a, b) => a.start - b.start)
    const pieces: { text: string; type: string | null }[] = []
    let at = 0
    for (const s of own) {
      if (s.start < at) continue
      if (s.start > at) pieces.push({ text: c.text.slice(at, s.start), type: null })
      pieces.push({ text: c.text.slice(s.start, s.start + s.length), type: s.type })
      at = s.start + s.length
    }
    if (at < c.text.length) pieces.push({ text: c.text.slice(at), type: null })
    return { ordinal: c.ordinal, pieces, matches: own.length }
  })
  .filter((c) => c.matches > 0))

const typeName = (type: string) => PREVIEW.piiTypes[type] ?? type

async function showHighlights(): Promise<void> {
  highlightsOpen.value = true
  startError.value = null
  outputError.value = false
  chunks.value = []
  spans.value = []
  try {
    const { jobId } = await api.documents.piiPreview(props.documentId)
    highlights.start(jobId)
  } catch (e) {
    startError.value = e
  }
}

watch(() => highlights.job.value?.state, async (state) => {
  if (state !== 'succeeded' || !highlights.job.value) return
  try {
    const jobId = highlights.job.value.id
    const [chunkOutput, piiOutput] = await Promise.all([
      api.previews.output<PreviewChunks>(jobId, props.documentId, 'chunk'),
      api.previews.output<PreviewPii>(jobId, props.documentId, 'personal-data'),
    ])
    chunks.value = chunkOutput.value?.chunks ?? []
    spans.value = piiOutput.value?.spans ?? []
  } catch {
    outputError.value = true
  }
})

function openClear(): void {
  reason.value = ''
  reasonError.value = null
  clearError.value = null
  clearNotice.value = null
  clearOpen.value = true
}

async function clear(): Promise<void> {
  if (reason.value.trim().length < MIN_REASON) {
    reasonError.value = L.reasonTooShort
    return
  }
  reasonError.value = null
  clearError.value = null
  clearNotice.value = null
  sending.value = true
  try {
    const { jobId } = await api.documents.piiClear(props.documentId, reason.value.trim())
    clearing.start(jobId)
  } catch (e) {
    clearError.value = e
  } finally {
    sending.value = false
  }
}

// A failed status read while clearing: say that the outcome is not known, so the admin checks the document (#963 review).
watch(() => clearing.error.value, (error) => {
  if (error) clearNotice.value = L.clearUnknown
})

watch(() => clearing.job.value?.state, (state) => {
  if (!state || clearing.polling.value) return
  if (state === 'succeeded') {
    clearOpen.value = false
    ui.notify(L.cleared)
    emit('cleared')
  } else {
    clearNotice.value = L.clearFailed
  }
})
</script>

<template>
  <span class="flex flex-wrap gap-1">
    <UiButton size="sm" data-test="pii-highlights" @click="showHighlights">{{ L.showHighlights }}</UiButton>
    <UiButton size="sm" data-test="pii-clear" @click="openClear">{{ L.clear }}</UiButton>
  </span>

  <UiDrawer v-model:open="highlightsOpen" :title="L.highlightsTitle(title)" wide>
    <p class="mb-4 text-fg-2">{{ L.highlightsLead }}</p>
    <p v-if="highlightsState === 'running'" role="status" data-test="pii-highlights-running">{{ L.running }}</p>
    <InlineError v-else-if="highlightsState === 'failed'" :message="startError ? problemMessage(startError) : L.failed" data-test="pii-highlights-failed" />
    <p v-else-if="!marked.length" data-test="pii-highlights-none">{{ L.none }}</p>
    <ol v-else class="grid gap-4" data-test="pii-highlights-list">
      <li v-for="c in marked" :key="c.ordinal" class="grid gap-1">
        <span class="text-small text-fg-3">{{ L.passage(c.ordinal + 1) }}</span>
        <p class="whitespace-pre-wrap rounded-card border border-border p-3" lang="sv">
          <template v-for="(p, i) in c.pieces" :key="i">
            <!-- eslint-disable-next-line vue/max-len -->
            <mark v-if="p.type" class="rounded bg-warn-soft px-0.5 font-medium text-fg" :title="L.match(typeName(p.type))"><span class="sr-only">{{ L.match(typeName(p.type)) }}: </span>{{ p.text }}</mark>
            <template v-else>{{ p.text }}</template>
          </template>
        </p>
      </li>
    </ol>
  </UiDrawer>

  <UiDialog :open="clearOpen" :title="L.clearTitle(title)" tone="neutral" @update:open="clearOpen = $event">
    <template #description>{{ L.clearConsequence }}</template>
    <form id="pii-clear-form" class="grid gap-3" novalidate @submit.prevent="clear">
      <TextAreaField id="pii-clear-reason" v-model="reason" :label="L.reason" :hint="L.reasonHint" :rows="3" :error="reasonError" />
      <p v-if="clearing.polling.value" role="status">{{ L.clearing }}</p>
      <InlineError v-if="clearNotice || clearError" :message="clearNotice ?? problemMessage(clearError)" data-test="pii-clear-error" />
    </form>
    <template #actions>
      <UiButton @click="clearOpen = false">{{ COMMON.cancel }}</UiButton>
      <UiButton variant="primary" type="submit" form="pii-clear-form" :loading="sending || clearing.polling.value" data-test="pii-clear-confirm">{{ L.clear }}</UiButton>
    </template>
  </UiDialog>
</template>
