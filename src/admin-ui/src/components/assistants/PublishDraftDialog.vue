<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { RuleWarning, VersionDiff } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import { useLoadable } from '@/stores/loadable'
import { ASSISTANT_TESTS, COMMON, PUBLISH_DIALOG as L } from '@/utils/labels'
import { ERROR_MESSAGES, formatMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import VersionDiffView from './VersionDiffView.vue'

/**
 * Publish draft's dialog (admin design §5.5.4, B-82): what publishing changes against the published version (B-56's diff;
 * absent on a first publish), the draft's remaining warnings, of which a refusal blocks Publish, V36's confirmation for a
 * public assistant with general knowledge as a checkbox here rather than a dialog of its own, and the test run's
 * estimated model calls when the assistant has test questions (B-57, §7.4). The server stays the judge: a refusal it
 * still answers shows inline.
 *
 * The warnings are read again from the draft when the dialog opens, since a fix in *Edit name and contact* or the
 * audience changes the assistant, not the draft, and the page's copy is from the draft's last load or save. *Publish*
 * waits for that read; the diff and the cost don't hold it up.
 */
const props = defineProps<{
  open: boolean
  assistantId: string
  /** The published version's number; null before the first publish. */
  publishedVersion: number | null
  /** The page's copy of the draft's warnings, shown until the dialog's own read of the draft answers. */
  warnings: RuleWarning[]
  /** V36 (#445): a public assistant whose draft uses general knowledge. */
  needsGeneralKnowledgeConfirmation: boolean
  /** Publishes; true confirms V36. The dialog closes when it resolves and shows the error when it rejects. */
  action: (confirmGeneralKnowledge: boolean) => Promise<unknown>
}>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void }>()

/** Warnings that do not stop a publish: the gateway key's (V39, a draft-save note) and V36, confirmed below. */
const NOT_REFUSALS = new Set(['gateway_key_required', 'gateway_key_system_prompt', 'general_knowledge_public_unconfirmed'])
const GENERAL_KNOWLEDGE = 'general_knowledge_public_unconfirmed'

/**
 * Which warnings refuse a publish. The draft's warnings are the publish check's problems followed by V39's notes, so
 * `gateway_key_required` comes twice once the gateway acts as keys (B-43): one is the publish check's refusal, one the
 * note. A note code seen twice is therefore a refusal, shown once.
 */
function classify(warnings: RuleWarning[]): { refusals: RuleWarning[]; notes: RuleWarning[] } {
  const refusals: RuleWarning[] = []
  const notes: RuleWarning[] = []
  const seen = new Set<string>()
  for (const w of warnings) {
    if (w.code === GENERAL_KNOWLEDGE) continue
    if (!NOT_REFUSALS.has(w.code)) refusals.push(w)
    else if (warnings.filter((o) => o.code === w.code).length > 1) {
      if (!seen.has(w.code)) refusals.push(w)
    } else notes.push(w)
    seen.add(w.code)
  }
  return { refusals, notes }
}

const diff = useLoadable<VersionDiff | null>(null)
const questions = ref<number | null>(null)
const confirmed = ref(false)
const busy = ref(false)
const error = ref<unknown>(null)
/** The draft's warnings as of opening the dialog; null while that read runs (or when it failed: the page's copy). */
const fresh = ref<RuleWarning[] | null>(null)
const checking = ref(false)
/** The server asked for V36's confirmation although the page did not expect it. */
const askedForConfirmation = ref(false)

const text = (w: RuleWarning) => formatMessage(ERROR_MESSAGES[w.code] ?? w.code, w.params ?? undefined) + (w.affected.length ? ` (${w.affected.join(', ')})` : '')
const warnings = computed(() => fresh.value ?? props.warnings)
const classified = computed(() => classify(warnings.value))
const refusals = computed(() => classified.value.refusals)
const notes = computed(() => classified.value.notes)
const needsConfirmation = computed(() =>
  props.needsGeneralKnowledgeConfirmation || askedForConfirmation.value || warnings.value.some((w) => w.code === GENERAL_KNOWLEDGE))
const blocked = computed(() => checking.value || refusals.value.length > 0 || (needsConfirmation.value && !confirmed.value))

watch(
  () => props.open,
  (open) => {
    if (!open) return
    confirmed.value = false
    error.value = null
    questions.value = null
    fresh.value = null
    askedForConfirmation.value = false
    checking.value = true
    void api.assistants.draft(props.assistantId)
      .then((d) => (fresh.value = d.warnings ?? []))
      .catch(() => undefined) // the page's copy stands; the server judges the publish
      .finally(() => (checking.value = false))
    if (props.publishedVersion !== null) void diff.run(() => api.assistants.diff(props.assistantId, props.publishedVersion!, 'draft'))
    void api.assistantTests.questions(props.assistantId).then((q) => (questions.value = q.length)).catch(() => (questions.value = null))
  },
  { immediate: true },
)

async function publish(): Promise<void> {
  if (blocked.value) return
  busy.value = true
  error.value = null
  try {
    await props.action(confirmed.value)
    emit('update:open', false)
  } catch (e) {
    error.value = e
    // V36 refused although the page did not expect it (its copy of the draft was stale or missing): ask here.
    if (e instanceof ApiError && e.problem?.code === GENERAL_KNOWLEDGE) askedForConfirmation.value = true
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiDialog :open="open" :title="publishedVersion === null ? L.titleFirst : L.title(publishedVersion)" wide @update:open="emit('update:open', $event)">
    <section v-if="publishedVersion !== null" aria-labelledby="publish-changes" data-test="publish-diff">
      <h3 id="publish-changes" class="mb-2 font-medium">{{ L.changes(publishedVersion) }}</h3>
      <AsyncState :loading="diff.loading.value" :error="diff.error.value" @retry="diff.run(() => api.assistants.diff(assistantId, publishedVersion!, 'draft'))">
        <div class="max-h-[40vh] overflow-auto"><VersionDiffView v-if="diff.data.value" :diff="diff.data.value" /></div>
      </AsyncState>
    </section>
    <p v-else class="text-fg-2" data-test="publish-first">{{ L.first }}</p>

    <section v-if="refusals.length || notes.length" aria-labelledby="publish-warnings" data-test="publish-warnings">
      <h3 id="publish-warnings" class="mb-2 font-medium">{{ refusals.length ? L.refusals : L.notes }}</h3>
      <ul class="ml-5 list-disc">
        <li v-for="w in refusals" :key="w.code + w.affected.join()" class="text-danger" data-test="publish-refusal">{{ text(w) }}</li>
        <li v-for="w in notes" :key="w.code + w.affected.join()" class="text-warn" data-test="publish-note">{{ text(w) }}</li>
      </ul>
    </section>

    <CheckField
      v-if="needsConfirmation" v-model="confirmed" :label="L.confirmGeneralKnowledge" :description="L.confirmGeneralKnowledgeHint"
      data-test="publish-confirm-general-knowledge"
    />

    <p v-if="questions" class="text-small text-fg-2" data-test="publish-cost">{{ L.testRun(ASSISTANT_TESTS.cost(questions)) }}</p>
    <InlineError v-if="error" :message="problemMessage(error)" />

    <template #actions>
      <UiButton @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
      <UiButton variant="primary" :disabled="blocked" :loading="busy" data-test="publish-confirm" @click="publish">{{ L.publish }}</UiButton>
    </template>
  </UiDialog>
</template>
