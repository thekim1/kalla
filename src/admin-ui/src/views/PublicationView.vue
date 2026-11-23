<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { EvaluationRun, Pipeline, Publication, RunSummary, Source } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { FINISHED_STATES } from '@/composables/useJobPoll'
import { usePoll } from '@/composables/usePoll'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatNumber, formatUsdCost } from '@/utils/format'
import { PUBLICATION, RUN_STATE_LABEL } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import { runStateTone } from '@/utils/runs'

// Publicering (admin design §6.6, §6.7; P1b item 11): the plan of a pipeline publication (documents re-processed, time and
// cost, the sources and their collections), Bekräfta (409 when the pipeline changed since the plan), the re-processing of
// each source, the evaluation report of each collection after it (publish-report), and Behåll / Återställ (rollback to
// the version published before). Everything is read from the API; nothing here is stored.
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const publication = useLoadable<Publication | null>(null)
const pipeline = ref<Pipeline | null>(null)
const sources = ref<Record<string, Source>>({})
const runs = ref<Record<string, RunSummary | null>>({})
const reports = ref<Record<string, { run: EvaluationRun; compared: EvaluationRun | null } | null>>({})
const confirming = ref(false)
const rollingBack = ref(false)
const kept = ref(false)
const canPublish = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-pipeline-editor']))

const plan = computed(() => publication.data.value?.plan ?? null)
const version = computed(() => pipeline.value?.versions.find((v) => v.id === publication.data.value?.pipelineVersionId) ?? null)
const publishedAt = computed(() => version.value?.publishedAt ?? null)
const planning = computed(() => !!publication.data.value && !FINISHED_STATES.has(publication.data.value.state))
const planFailed = computed(() => !!publication.data.value && FINISHED_STATES.has(publication.data.value.state) && publication.data.value.state !== 'succeeded')
const published = computed(() => !!publication.data.value?.published || version.value?.state === 'published' || version.value?.state === 'superseded')
const rolledBack = computed(() => published.value && version.value?.state === 'superseded')
const sourceIds = computed(() => plan.value?.sources.map((s) => s.sourceId) ?? [])
const collectionIds = computed(() => [...new Set(Object.values(sources.value).map((s) => s.collectionId))])
const reprocessed = computed(() => sourceIds.value.length > 0 && sourceIds.value.every((id) => runs.value[id] && runs.value[id]!.state !== 'running'))
const reported = computed(() => collectionIds.value.length > 0 && collectionIds.value.every((id) => reports.value[id]))
/** The version published before this one: the target of Återställ. */
const previous = computed(() =>
  (pipeline.value?.versions ?? []).filter((v) => v.state === 'superseded' && v.number < (version.value?.number ?? 0)).sort((a, b) => b.number - a.number)[0] ?? null,
)

/** The source's re-processing run of this publication: the newest publish-triggered run since the version was published. */
async function loadRun(sourceId: string): Promise<void> {
  const since = publishedAt.value
  if (!since) return
  const page = await api.sources.runs(sourceId).catch(() => null)
  runs.value = { ...runs.value, [sourceId]: page?.items.find((r) => r.trigger === 'publish' && r.startedAt >= since) ?? null }
}

/** The collection's publish-report since the version was published, with the run it compares against. */
async function loadReport(collectionId: string): Promise<void> {
  const since = publishedAt.value
  if (!since) return
  const overview = await api.collections.evaluation(collectionId).catch(() => null)
  const run = overview?.latestRuns.find((r) => r.kind === 'publish-report' && r.startedAt >= since && r.finishedAt) ?? null
  reports.value = { ...reports.value, [collectionId]: run ? { run, compared: overview!.latestRuns.find((r) => r.id === run.comparedRunId) ?? null } : null }
}

async function refresh(): Promise<void> {
  const p = await api.publications.get(props.id)
  publication.data.value = p
  pipeline.value = await api.pipelines.get(p.pipelineId)
  const missing = (p.plan?.sources ?? []).map((s) => s.sourceId).filter((id) => !sources.value[id])
  if (missing.length) {
    const loaded = await Promise.all(missing.map((id) => api.sources.get(id).catch(() => null)))
    sources.value = { ...sources.value, ...Object.fromEntries(loaded.filter((s): s is Source => !!s).map((s) => [s.id, s])) }
  }
  if (published.value) {
    await Promise.all(sourceIds.value.map(loadRun))
    if (reprocessed.value) await Promise.all(collectionIds.value.map(loadReport))
  }
}

// Polls while something is still moving: the plan, the re-processing, the evaluation report (§4.7 delays). A failed
// refresh ends the poll and shows its error.
const moving = computed(() => planning.value || (published.value && !rolledBack.value && !kept.value && !(reprocessed.value && reported.value)))
const poll = usePoll(async () => {
  await refresh()
  return !moving.value
})
const error = poll.error

function schedule(): void {
  if (moving.value) poll.start(false)
  else poll.stop()
}

async function load(): Promise<void> {
  await publication.run(async () => {
    await refresh()
    return publication.data.value
  })
  schedule()
}

async function confirm(): Promise<void> {
  publication.data.value = await api.publications.confirm(props.id, publication.data.value!.version)
  ui.notify(PUBLICATION.publishedDone)
  await refresh()
  schedule()
}

async function rollback(): Promise<void> {
  const target = previous.value!
  await api.pipelines.rollback(pipeline.value!.id, { versionId: target.id, version: pipeline.value!.version })
  ui.notify(PUBLICATION.rolledBackDone(target.number))
  await refresh()
}

onMounted(load)
</script>

<template>
  <AsyncState :loading="publication.loading.value && !publication.data.value" :error="publication.error.value" @retry="load">
    <template v-if="publication.data.value">
      <PageHeader :title="PUBLICATION.title" :description="pipeline ? PUBLICATION.lead(pipeline.name, publication.data.value.draftNumber ?? 0) : undefined">
        <UiBadge v-if="rolledBack" tone="warn" :label="PUBLICATION.rolledBack" data-test="state" />
        <UiBadge v-else-if="published" tone="ok" :label="PUBLICATION.published" data-test="state" />
        <UiBadge v-else-if="publication.data.value.superseded" tone="warn" :label="PUBLICATION.superseded" data-test="state" />
      </PageHeader>
      <InlineError v-if="error" :message="problemMessage(error)" class="mb-4" />

      <div class="grid max-w-4xl gap-5">
        <UiCard :title="PUBLICATION.plan" data-test="plan">
          <p v-if="planning" class="text-fg-2">{{ PUBLICATION.planning }}</p>
          <InlineError v-else-if="planFailed" :message="jobMessage(publication.data.value.lastError ?? 'preview.invalid_payload')" />
          <template v-else-if="plan">
            <p class="mb-3">{{ PUBLICATION.planSummary(formatNumber(plan.documents), plan.sources.length) }}</p>
            <dl class="grid gap-1">
              <DetailRow :label="PUBLICATION.estimatedTime">{{ PUBLICATION.minutes(plan.estimatedMinutes ?? 0) }}</DetailRow>
              <DetailRow :label="PUBLICATION.estimatedCost">{{ formatUsdCost(plan.estimatedCostUsd ?? 0) }}</DetailRow>
              <DetailRow v-if="plan.parserPages" :label="PUBLICATION.parserPages">{{ formatNumber(plan.parserPages) }}</DetailRow>
            </dl>
            <p class="mt-3 text-small text-fg-2">{{ PUBLICATION.planNote }}</p>
            <h3 class="mb-1 mt-4 font-medium">{{ PUBLICATION.affected }}</h3>
            <ul class="grid gap-1" data-test="affected">
              <li v-for="s in plan.sources" :key="s.sourceId">
                <RouterLink :to="{ name: 'source', params: { id: s.sourceId } }" class="link">{{ sources[s.sourceId]?.name ?? s.sourceId }}</RouterLink>
                <span class="text-fg-2"> · {{ PUBLICATION.documents(formatNumber(s.documents)) }}</span>
                <span v-if="published" class="ml-2">
                  <UiBadge :tone="runStateTone(runs[s.sourceId]?.state)" :label="runs[s.sourceId] ? (RUN_STATE_LABEL[runs[s.sourceId]!.state] ?? runs[s.sourceId]!.state) : PUBLICATION.waiting" />
                  <RouterLink v-if="runs[s.sourceId]" :to="{ name: 'run', params: { id: runs[s.sourceId]!.id } }" class="link ml-2 text-small">{{ PUBLICATION.openRun }}</RouterLink>
                </span>
              </li>
            </ul>
            <p v-if="publication.data.value.superseded && !published" class="mt-3 text-warn">{{ PUBLICATION.supersededHint }}</p>
            <div v-else-if="!published && canPublish" class="mt-4">
              <UiButton variant="primary" data-test="confirm" @click="confirming = true">{{ PUBLICATION.confirm }}</UiButton>
            </div>
          </template>
        </UiCard>

        <UiCard v-if="published" :title="PUBLICATION.report" data-test="report">
          <p v-if="!reprocessed" class="text-fg-2">{{ PUBLICATION.reprocessing(publishedAt ? formatDateTime(publishedAt) : '') }}</p>
          <template v-else>
            <div v-for="c in collectionIds" :key="c" class="mb-3">
              <template v-if="reports[c]">
                <p class="font-medium">{{ PUBLICATION.accuracy(reports[c]!.run.recallAtK, reports[c]!.run.questionCount ?? 0) }}</p>
                <p v-if="reports[c]!.compared" class="text-small text-fg-2">
                  {{ PUBLICATION.compared(reports[c]!.compared!.recallAtK, reports[c]!.run.wins ?? 0, reports[c]!.run.losses ?? 0) }}
                </p>
              </template>
              <p v-else class="text-fg-2">{{ PUBLICATION.evaluating }}</p>
            </div>
          </template>
          <p v-if="rolledBack" class="mt-2">{{ PUBLICATION.rolledBackHint }}</p>
          <p v-else-if="kept" class="mt-2">{{ PUBLICATION.keptHint }}</p>
          <div v-else-if="canPublish" class="mt-4 flex flex-wrap gap-2">
            <UiButton variant="primary" data-test="keep" @click="kept = true">{{ PUBLICATION.keep }}</UiButton>
            <UiButton v-if="previous" data-test="rollback" @click="rollingBack = true">{{ PUBLICATION.rollback }}</UiButton>
          </div>
        </UiCard>
      </div>

      <ConfirmDialog
        v-model:open="confirming" :title="PUBLICATION.confirmTitle" :consequence="PUBLICATION.confirmConsequence" :confirm-label="PUBLICATION.confirm"
        :action="confirm"
      />
      <ConfirmDialog
        v-model:open="rollingBack" :title="PUBLICATION.rollbackTitle(previous?.number ?? 0)" :consequence="PUBLICATION.rollbackConsequence(previous?.number ?? 0)"
        :confirm-label="PUBLICATION.rollback" :action="rollback"
      />
    </template>
  </AsyncState>
</template>
