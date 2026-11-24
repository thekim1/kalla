<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api } from '@/api'
import type { Publication, SourceTopicSuggestions, Topic } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { FINISHED_STATES } from '@/composables/useJobPoll'
import { usePoll } from '@/composables/usePoll'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatNumber, quietZero } from '@/utils/format'
import { TOPICS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { normaliseTag, ruleInWords, tagError, topicSources } from '@/utils/topics'
import TopicSuggestionsBlock from './TopicSuggestionsBlock.vue'

// Topics (admin design §5.5.2; Decisions "Topics editor"): the tags the sources' path rules put on the chunks, one row per
// tag with its rules in words, their sources and the live document count (*pending* until a new rule's re-chunk is done).
// The owner passes the knowledge base's sources. `EditPipelines` holders of the owner also get *Suggested from {source}*:
// ticked sections become `path` rules, saved by **Add N topics** through one publication plan per processing (§6.6, with
// the copy-on-write of a platform processing), all confirmed in one dialog with each plan's own document count.
// `Edit` and `Read` see the list read-only; a department the knowledge base is shared with sees tags and counts only.
const props = defineProps<{ collectionId: string; sources?: { id: string; name: string }[] }>()
const auth = useAuthStore()
const ui = useUiStore()
const topics = useLoadable<Topic[]>([])
const suggestions = useLoadable<SourceTopicSuggestions[]>([])
const owner = computed(() => props.sources !== undefined)
const canEditTopics = computed(() => owner.value && auth.can('edit-pipelines'))
const columns = computed<Column[]>(() => [
  { key: 'tag', label: TOPICS.topic },
  ...(owner.value ? [{ key: 'rule', label: TOPICS.rule }, { key: 'source', label: TOPICS.source }] : []),
  { key: 'documents', label: TOPICS.documents, align: 'right' as const },
])

/** The ticked sections per source: path → the tag as typed. */
const picks = reactive<Record<string, Record<string, string>>>({})
const pickList = computed(() => Object.entries(picks).flatMap(([sourceId, paths]) => Object.entries(paths).map(([path, tag]) => ({ sourceId, path, tag }))))
/** One topic per tag: two sections on the same tag are one topic with two rules. */
const newTopics = computed(() => new Set(pickList.value.map((p) => normaliseTag(p.tag))).size)
const picksValid = computed(() => pickList.value.length > 0 && pickList.value.every((p) => tagError(p.tag) === null))

function pick(sourceId: string, path: string, tag: string | null): void {
  const paths = (picks[sourceId] ??= {})
  if (tag === null) delete paths[path]
  else paths[path] = tag
}

async function loadTopics(): Promise<void> {
  await topics.run(async () => (await api.collections.topics(props.collectionId)).topics)
}

async function loadSuggestions(): Promise<void> {
  await suggestions.run(async () => (await api.collections.topicSuggestions(props.collectionId)).sources)
}

function load(): void {
  void loadTopics()
  if (canEditTopics.value) void loadSuggestions()
}

// Saving (§5.5.2, §6.6): a plan per processing with its published rules and the new ones; once every plan is made, one
// dialog lists each plan's own count and the affected sources, and Confirm publishes them all.
interface Planned { id: string; processing: string; publication: Publication | null }
const planned = ref<Planned[]>([])
const planning = ref(false)
const saveError = ref<string | null>(null)
const confirming = ref(false)
const sourceNames = computed(() => new Map((props.sources ?? []).map((s) => [s.id, s.name])))

async function add(): Promise<void> {
  if (!picksValid.value || planning.value) return
  planning.value = true
  saveError.value = null
  planned.value = []
  try {
    const bySource = Object.entries(picks).filter(([, paths]) => Object.keys(paths).length > 0)
    const loaded = await Promise.all(bySource.map(([sourceId]) => api.sources.get(sourceId)))
    const byPipeline = new Map<string, { match: string; on: 'path'; tags: string[] }[]>()
    loaded.forEach((source, i) => {
      const rules = Object.entries(bySource[i]![1]).map(([path, tag]) => ({ match: path, on: 'path' as const, tags: [normaliseTag(tag)] }))
      byPipeline.set(source.pipelineId, [...(byPipeline.get(source.pipelineId) ?? []), ...rules])
    })
    for (const [pipelineId, rules] of byPipeline) {
      const pipeline = await api.pipelines.get(pipelineId)
      const published = pipeline.versions.find((v) => v.id === pipeline.publishedVersionId)
      const params = { ...(published?.definition.params ?? {}) }
      params.pathRules = [...(Array.isArray(params.pathRules) ? params.pathRules : []), ...rules]
      // A department's first publication on a platform processing copies it for this knowledge base (§6.6).
      const copyOnWrite = pipeline.departmentId === null && !auth.isPlatformAdmin
      const started = await api.pipelines.plan(pipelineId, {
        parameters: params, version: pipeline.version, ...(copyOnWrite ? { collectionId: props.collectionId } : {}),
      })
      planned.value = [...planned.value, { id: started.publicationId, processing: pipeline.name, publication: null }]
    }
    poll.start()
  } catch (e) {
    saveError.value = problemMessage(e)
    planning.value = false
  }
}

const poll = usePoll(async (current) => {
  const answers = await Promise.all(planned.value.map((p) => (p.publication && FINISHED_STATES.has(p.publication.state) ? p.publication : api.publications.get(p.id))))
  if (!current()) return true
  planned.value = planned.value.map((p, i) => ({ ...p, publication: answers[i]! }))
  if (!answers.every((a) => FINISHED_STATES.has(a.state))) return false
  planning.value = false
  const failed = planned.value.find((p) => p.publication!.state !== 'succeeded' || !p.publication!.plan)
  if (failed) saveError.value = TOPICS.planFailed(failed.processing)
  else confirming.value = true
  return true
})

/** The sources a plan processes again: the knowledge base's by name, others counted. */
function affected(p: Planned): string {
  const ids = p.publication?.plan?.sources.map((s) => s.sourceId) ?? []
  const names = ids.map((id) => sourceNames.value.get(id)).filter((n): n is string => !!n)
  const others = ids.length - names.length
  return [...names, ...(others > 0 ? [TOPICS.otherSources(others)] : [])].join(', ')
}

async function confirm(): Promise<void> {
  for (const p of planned.value) await api.publications.confirm(p.id, p.publication!.version)
  const n = newTopics.value
  for (const sourceId of Object.keys(picks)) delete picks[sourceId]
  planned.value = []
  ui.notify(TOPICS.saved(n))
  load()
}

onMounted(load)
</script>

<template>
  <div class="grid gap-6">
    <div class="grid gap-4">
      <p class="m-0 text-fg-2">{{ TOPICS.lead }}</p>
      <p v-if="owner && !canEditTopics" class="m-0 text-fg-2" data-test="topics-read-only">{{ TOPICS.readOnly }}</p>
      <div v-if="canEditTopics && props.sources?.length" class="grid gap-2" data-test="topics-help">
        <p class="m-0">
          {{ TOPICS.editRules }}
          <template v-for="(s, i) in props.sources" :key="s.id"><span v-if="i > 0">, </span><RouterLink :to="{ name: 'source', params: { id: s.id } }" class="text-accent-ink underline" data-test="topics-source">{{ s.name }}</RouterLink></template>
        </p>
        <details>
          <summary class="cursor-pointer">{{ TOPICS.howTitle }}</summary>
          <div class="mt-2 grid gap-2">
            <p class="m-0 text-fg-2">{{ TOPICS.how }}</p>
            <pre class="m-0 overflow-x-auto rounded-card bg-sunken p-3 text-small font-mono">{{ TOPICS.example }}</pre>
            <p class="m-0 text-fg-2">{{ TOPICS.after }}</p>
          </div>
        </details>
      </div>
      <AsyncState :loading="topics.loading.value" :error="topics.error.value" :empty="topics.data.value.length === 0" :empty-text="TOPICS.none" @retry="loadTopics">
        <DataTable :columns="columns" :rows="topics.data.value" :row-key="(t) => t.tag" :caption="TOPICS.topic" data-test="topics">
          <template #cell-tag="{ row }"><span class="font-mono">{{ row.tag }}</span></template>
          <template #cell-rule="{ row }">
            <span v-if="row.rules?.length" class="grid gap-0.5">
              <span v-for="r in row.rules" :key="`${r.sourceId}-${r.on}-${r.match}`" data-test="topic-rule">{{ ruleInWords(r) }}</span>
            </span>
            <span v-else class="text-fg-3">{{ TOPICS.noRule }}</span>
          </template>
          <template #cell-source="{ row }">{{ topicSources(row).join(', ') }}</template>
          <template #cell-documents="{ row }">
            <span v-if="row.pending" class="text-fg-3" data-test="topic-pending">{{ TOPICS.pending }}</span>
            <span v-else class="tabular" :class="quietZero(row.documents ?? 0)">{{ formatNumber(row.documents ?? 0) }}</span>
          </template>
        </DataTable>
      </AsyncState>
    </div>

    <template v-if="canEditTopics">
      <AsyncState :loading="suggestions.loading.value" :error="suggestions.error.value" @retry="loadSuggestions">
        <div class="grid gap-6">
          <TopicSuggestionsBlock
            v-for="block in suggestions.data.value" :key="block.sourceId" :collection-id="collectionId" :block="block" :picks="picks[block.sourceId] ?? {}"
            @pick="(path, tag) => pick(block.sourceId, path, tag)"
          />
          <div v-if="suggestions.data.value.some((b) => b.sections.length > 0)" class="grid justify-items-end gap-2">
            <p v-if="planning" class="m-0 text-small text-fg-2" role="status" data-test="topics-planning">{{ TOPICS.planning }}</p>
            <InlineError v-if="saveError" :message="saveError" data-test="topics-save-error" />
            <UiButton variant="primary" icon="add" :disabled="!picksValid || planning" data-test="add-topics" @click="add">{{ TOPICS.add(newTopics) }}</UiButton>
          </div>
        </div>
      </AsyncState>
      <ConfirmDialog v-model:open="confirming" :title="TOPICS.saveTitle(newTopics)" :confirm-label="TOPICS.saveConfirm" :action="confirm">
        <div class="grid gap-3" data-test="topics-plan">
          <p class="m-0">{{ TOPICS.saveLead }}</p>
          <ul class="m-0 grid gap-2 pl-5">
            <li v-for="p in planned" :key="p.id" data-test="topics-plan-item">
              <span class="block">{{ TOPICS.savePlan(p.processing, formatNumber(p.publication?.plan?.documents ?? 0)) }}</span>
              <span class="block text-small text-fg-2">{{ TOPICS.saveSources(affected(p)) }}</span>
            </li>
          </ul>
        </div>
      </ConfirmDialog>
    </template>
  </div>
</template>
