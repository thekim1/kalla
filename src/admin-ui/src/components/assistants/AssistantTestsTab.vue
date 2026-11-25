<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Assistant, TestQuestion, TestResult, TestRunReport, TestRunSummary } from '@/api/types'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { usePoll } from '@/composables/usePoll'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { ASSISTANT_TESTS as L } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import TestQuestionDrawer from './TestQuestionDrawer.vue'

// Test questions (admin design §7.4, B-57): the assistant's set, Run test questions, and the latest run as the §7.2 card with
// the changed questions and the judge's reasons; after a publish, Keep or Restore the previous version.
const props = defineProps<{ assistant: Assistant; canEdit: boolean; collections: { id: string; name: string }[] }>()
const emit = defineEmits<{ (e: 'restored', assistant: Assistant): void; (e: 'count', n: number): void }>()
const ui = useUiStore()

const questions = ref<TestQuestion[]>([])
const runs = ref<TestRunSummary[]>([])
const report = ref<TestRunReport | null>(null)
const error = ref<unknown>(null)
const editing = ref<TestQuestion | null>(null)
const drawerOpen = ref(false)
const deleting = ref<TestQuestion | null>(null)
const restoring = ref(false)
const kept = ref<string | null>(null)
const target = ref<'Draft' | 'Published'>(props.assistant.draft ? 'Draft' : 'Published')
const starting = ref(false)
const initial = ref('')

const latest = computed(() => runs.value[0] ?? null)
const running = computed(() => latest.value !== null && latest.value.finishedAt === null)
const targetOptions = computed(() => (['Draft', 'Published'] as const)
  .filter((t) => (t === 'Draft' ? !!props.assistant.draft : props.assistant.publishedVersion !== null))
  .map((t) => ({ value: t, label: L.runTargets[t] })))
const changed = computed(() => report.value?.results.filter((r) => r.change) ?? [])
// After a publish: the latest run is on the published version and has one to compare with.
const decide = computed(() => {
  const run = latest.value
  return run && run.finishedAt && run.version === props.assistant.publishedVersion && run.comparedVersion !== null && kept.value !== run.id
    ? run
    : null
})

async function fetchAll(): Promise<void> {
  error.value = null
  try {
    ;[questions.value, runs.value] = await Promise.all([api.assistantTests.questions(props.assistant.id), api.assistantTests.runs(props.assistant.id)])
    emit('count', questions.value.length)
    report.value = latest.value ? await api.assistantTests.report(props.assistant.id, latest.value.id) : null
  } catch (e) {
    error.value = e
  }
}

// While the latest run is going, every 3 s until it has finished (usePoll, #980); the poll ends with the tab.
const poller = usePoll(async () => {
  await fetchAll()
  return !running.value
}, () => 3000)

async function load(): Promise<void> {
  poller.stop()
  await fetchAll()
  if (running.value) poller.start(false)
}

function add(): void {
  editing.value = null
  drawerOpen.value = true
}

function edit(q: TestQuestion): void {
  editing.value = q
  drawerOpen.value = true
}

function saved(): void {
  ui.notify(L.saved)
  void load()
}

async function remove(): Promise<void> {
  await api.assistantTests.remove(props.assistant.id, deleting.value!.id, deleting.value!.version)
  ui.notify(L.deleted)
  await load()
}

async function run(): Promise<void> {
  starting.value = true
  error.value = null
  try {
    await api.assistantTests.run(props.assistant.id, target.value)
    ui.notify(L.started)
    await load()
  } catch (e) {
    error.value = e
  } finally {
    starting.value = false
  }
}

async function restorePrevious(): Promise<void> {
  const restored = await api.assistants.restore(props.assistant.id, decide.value!.comparedVersion!, props.assistant.version, false)
  emit('restored', restored)
  await load()
}

function resultLine(r: TestResult): string[] {
  const lines: string[] = [r.asExpected ? L.asExpected : L.notAsExpected]
  if (r.checks !== null) lines.push(L.checks[String(r.checks)]!)
  if (r.verdict) lines.push(L.verdicts[r.verdict]!)
  return lines
}

onMounted(load)
defineExpose({ load })
</script>

<template>
  <div class="grid gap-5" data-test="test-questions">
    <p class="text-fg-2">{{ L.lead }}</p>
    <InlineError v-if="error" :message="problemMessage(error)" />

    <UiCard :title="L.report" data-test="test-report">
      <p v-if="!latest" class="text-fg-2">{{ L.noReport }}</p>
      <template v-else>
        <p v-if="running" role="status" data-test="test-running">{{ L.running(report?.results.length ?? 0, latest.questions) }}</p>
        <template v-else>
          <p class="font-medium" data-test="test-summary">
            {{ L.summary(latest.questions, latest.asExpected ?? 0) }}
            <span v-if="latest.comparedVersion !== null">{{ L.previous(latest.comparedVersion, latest.comparedAsExpected ?? 0) }}</span>
            <span v-if="latest.worse !== null"> · {{ L.changes(latest.worse, latest.better ?? 0) }}</span>
          </p>
          <p class="text-small text-fg-3">{{ L.ranOn(latest.version, formatDateTime(latest.finishedAt ?? latest.startedAt)) }}</p>
        </template>
        <div v-if="decide && canEdit" class="mt-3 flex flex-wrap gap-2" data-test="test-decide">
          <UiButton data-test="test-keep" @click="kept = decide.id">{{ L.keep }}</UiButton>
          <UiButton data-test="test-restore" @click="restoring = true">{{ L.restorePrevious(decide.comparedVersion!) }}</UiButton>
        </div>
        <details v-if="changed.length" open class="mt-4" data-test="test-changed">
          <summary class="cursor-pointer font-medium">{{ L.changed }} ({{ changed.length }})</summary>
          <ul class="mt-2 grid gap-3">
            <li v-for="r in changed" :key="r.questionId" class="grid gap-1">
              <span class="font-medium">{{ r.question }}</span>
              <UiBadge :tone="r.change === 'better' ? 'ok' : 'warn'" :label="r.change === 'better' ? L.better : L.worse" />
              <span v-if="r.judgeNote" class="text-small text-fg-2">{{ r.judgeNote }}</span>
            </li>
          </ul>
        </details>
        <details v-if="report?.results.length" class="mt-4" data-test="test-all">
          <summary class="cursor-pointer font-medium">{{ L.all }} ({{ report.results.length }})</summary>
          <ul class="mt-2 grid gap-4">
            <li v-for="r in report.results" :key="r.questionId" class="grid gap-1" :data-test="`test-result-${r.questionId}`">
              <span class="font-medium">{{ r.question }}</span>
              <span class="text-small">{{ resultLine(r).join(' · ') }}</span>
              <span v-if="r.judgeNote" class="text-small text-fg-2">{{ r.judgeNote }}</span>
              <details class="text-small">
                <summary class="cursor-pointer">{{ L.answer }}</summary>
                <p class="whitespace-pre-wrap text-fg-2">{{ r.answer ?? L.notAnswered }}</p>
              </details>
            </li>
          </ul>
        </details>
        <details class="mt-4 text-small">
          <summary class="cursor-pointer">{{ L.about }}</summary>
          <p class="mt-1 text-fg-2">{{ L.aboutText }}</p>
        </details>
      </template>
    </UiCard>

    <div v-if="canEdit && questions.length && targetOptions.length" class="flex flex-wrap items-end gap-3" data-test="test-run">
      <SelectField id="test-run-target" v-model="target" class="max-w-sm" :label="L.runOn" :options="targetOptions" />
      <UiButton variant="primary" :loading="starting" :disabled="running" data-test="test-run-start" @click="run">{{ L.run }}</UiButton>
      <p class="w-full text-small text-fg-3" data-test="test-cost">{{ L.cost(questions.length) }}</p>
    </div>

    <div class="flex items-center justify-between gap-3">
      <p class="text-small text-fg-3">{{ L.count(questions.length) }}</p>
      <UiButton v-if="canEdit && questions.length < 30" icon="add" data-test="test-question-add" @click="add">{{ L.add }}</UiButton>
    </div>
    <p v-if="!questions.length" class="text-fg-2">{{ L.empty }}</p>
    <ul v-else class="grid gap-3" data-test="test-question-list">
      <li v-for="q in questions" :key="q.id" class="grid gap-1 rounded-card border border-border p-3" :data-test="`test-question-${q.id}`">
        <span class="font-medium">{{ q.question }}</span>
        <span class="text-small text-fg-2">{{ q.expectedBehaviour }}</span>
        <span v-if="q.expectedDocument" class="text-small">
          {{ q.expectedDocument.title ?? q.expectedDocument.id }}
          <span v-if="q.expectedDocument.deleted" class="text-warn"> · {{ L.documentDeleted }}</span>
          <span v-else-if="q.expectedDocument.unlinked" class="text-warn"> · {{ L.documentUnlinked }}</span>
        </span>
        <span v-if="q.expectedPhrase" class="text-small">“{{ q.expectedPhrase }}”</span>
        <div v-if="canEdit" class="flex gap-2">
          <UiButton size="sm" icon="edit" @click="edit(q)">{{ L.edit }}</UiButton>
          <UiButton size="sm" variant="quiet" icon="delete" @click="deleting = q">{{ L.remove }}</UiButton>
        </div>
      </li>
    </ul>

    <TestQuestionDrawer
      v-model:open="drawerOpen" :assistant-id="assistant.id" :question="editing" :initial-question="initial" :collections="collections" @saved="saved"
    />
    <ConfirmDialog
      :open="deleting !== null" :title="L.deleteTitle" :consequence="L.deleteConsequence" :confirm-label="L.remove" danger :action="remove"
      @update:open="!$event && (deleting = null)"
    />
    <ConfirmDialog
      :open="restoring" :title="L.restorePrevious(decide?.comparedVersion ?? 0)" :consequence="L.restoreConsequence(decide?.comparedVersion ?? 0)"
      :confirm-label="L.restorePrevious(decide?.comparedVersion ?? 0)" :action="restorePrevious" @update:open="restoring = $event"
    />
  </div>
</template>
