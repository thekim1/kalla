<script setup lang="ts">
import { computed, onMounted, reactive, ref, toRef } from 'vue'
import { api } from '@/api'
import type { PathRuleJson, SourceTopicSuggestions, Topic } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import MenuButton from '@/components/ui/MenuButton.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { publishedRules, useTopicPublication, type RuleChange } from '@/composables/useTopicPublication'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatNumber, quietZero } from '@/utils/format'
import { TOPICS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { addRules, findRule, normaliseTag, removeTopic, replaceRule, ruleInWords, tagError, topicSources } from '@/utils/topics'
import TopicRuleForm from './TopicRuleForm.vue'
import TopicSuggestionsBlock from './TopicSuggestionsBlock.vue'

// Topics (admin design §5.5.2; Decisions "Topics editor"): the tags the sources' path rules put on the chunks, one row per
// tag with its rules in words, their sources and the live document count (*pending* until a new rule's re-chunk is done).
// The owner passes the knowledge base's sources. `EditPipelines` holders of the owner also get *Add topic*, *Edit* on each
// rule, *Remove topic* in the row's ⋯ menu, and *Suggested from {source}* with **Add N topics**. Every change is saved
// through one publication plan per processing, confirmed in one dialog with each plan's own count (useTopicPublication).
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
  ...(canEditTopics.value ? [{ key: 'actions', label: '', align: 'right' as const }] : []),
])
const sourceNames = computed(() => new Map((props.sources ?? []).map((s) => [s.id, s.name])))
const publication = useTopicPublication(toRef(props, 'collectionId'), sourceNames)
/** The toast once the dialog's Confirm has published. */
const doneMessage = ref('')

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

/** The picked sections as `path` rules, one plan per processing. */
function addPicked(): void {
  if (!picksValid.value) return
  const changes = new Map<string, RuleChange[]>()
  for (const p of pickList.value) {
    changes.set(p.sourceId, [...(changes.get(p.sourceId) ?? []), addRules([{ match: p.path, on: 'path', tags: [normaliseTag(p.tag)] }])])
  }
  doneMessage.value = TOPICS.saved(newTopics.value)
  removing.value = false
  void publication.plan(changes, TOPICS.saveTitle(newTopics.value))
}

// The form: Add topic, or Edit one rule of a topic (found as stored in its processing).
const formOpen = ref(false)
const editing = ref<{ sourceId: string; rule: PathRuleJson; tag: string } | null>(null)
const opening = ref(false)
const openError = ref<string | null>(null)

function addTopic(): void {
  editing.value = null
  formOpen.value = true
}

async function editRule(topic: Topic, rule: { sourceId: string; on: string; match: string }): Promise<void> {
  opening.value = true
  openError.value = null
  try {
    const source = await api.sources.get(rule.sourceId)
    const stored = publishedRules(await api.pipelines.get(source.pipelineId))
    const at = findRule(stored, topic.tag, rule.on, rule.match)
    editing.value = { sourceId: rule.sourceId, tag: topic.tag, rule: at >= 0 ? stored[at]! : { match: rule.match, on: rule.on as PathRuleJson['on'], tags: [topic.tag] } }
    formOpen.value = true
  } catch (e) {
    openError.value = problemMessage(e)
  } finally {
    opening.value = false
  }
}

function saveForm({ sourceId, rule }: { sourceId: string; rule: PathRuleJson }): void {
  const changes = new Map<string, RuleChange[]>()
  const was = editing.value
  const tag = rule.tags.map(normaliseTag)[0] ?? ''
  removing.value = false
  if (!was) {
    changes.set(sourceId, [addRules([rule])])
    doneMessage.value = TOPICS.saved(1)
    void publication.plan(changes, TOPICS.saveTitle(1))
    return
  }
  const replace = replaceRule(was.tag, was.rule.on, was.rule.match, rule)
  if (was.sourceId === sourceId) changes.set(sourceId, [replace])
  else {
    // Moved to another source: the old one loses this topic's rule, the new one gains it.
    changes.set(was.sourceId, [(rules) => replace(rules).filter((r) => r !== rule)])
    changes.set(sourceId, [addRules([rule])])
  }
  doneMessage.value = TOPICS.changed(tag)
  void publication.plan(changes, TOPICS.editTitle(tag))
}

const removing = ref(false)
function remove(topic: Topic): void {
  const changes = new Map<string, RuleChange[]>()
  for (const sourceId of new Set((topic.rules ?? []).map((r) => r.sourceId))) changes.set(sourceId, [removeTopic(topic.tag)])
  doneMessage.value = TOPICS.removed(topic.tag)
  removing.value = true
  void publication.plan(changes, TOPICS.removeTitle(topic.tag))
}

async function confirm(): Promise<void> {
  await publication.confirm()
  for (const sourceId of Object.keys(picks)) delete picks[sourceId]
  ui.notify(doneMessage.value)
  load()
}

onMounted(load)
</script>

<template>
  <div class="grid gap-6">
    <div class="grid gap-4">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <p class="m-0 max-w-[680px] text-fg-2">{{ TOPICS.lead }}</p>
        <UiButton v-if="canEditTopics" icon="add" data-test="add-topic" @click="addTopic">{{ TOPICS.addTopic }}</UiButton>
      </div>
      <p v-if="owner && !canEditTopics" class="m-0 text-fg-2" data-test="topics-read-only">{{ TOPICS.readOnly }}</p>
      <InlineError v-if="openError" :message="openError" />
      <AsyncState :loading="topics.loading.value" :error="topics.error.value" :empty="topics.data.value.length === 0" :empty-text="TOPICS.none" @retry="loadTopics">
        <DataTable :columns="columns" :rows="topics.data.value" :row-key="(t) => t.tag" :caption="TOPICS.topic" data-test="topics">
          <template #cell-tag="{ row }"><span class="font-mono">{{ row.tag }}</span></template>
          <template #cell-rule="{ row }">
            <span v-if="row.rules?.length" class="grid gap-0.5">
              <span v-for="r in row.rules" :key="`${r.sourceId}-${r.on}-${r.match}`" class="flex flex-wrap items-baseline gap-x-2" data-test="topic-rule">
                <span data-test="topic-rule-words">{{ ruleInWords(r) }}</span>
                <button
                  v-if="canEditTopics" type="button" class="text-small text-accent-ink underline" :aria-label="TOPICS.editRule(row.tag, ruleInWords(r))"
                  :disabled="opening" data-test="edit-rule" @click="editRule(row, r)"
                >{{ TOPICS.edit }}</button>
              </span>
            </span>
            <span v-else class="text-fg-3">{{ TOPICS.noRule }}</span>
          </template>
          <template #cell-source="{ row }">{{ topicSources(row).join(', ') }}</template>
          <template #cell-documents="{ row }">
            <span v-if="row.pending" class="text-fg-3" data-test="topic-pending">{{ TOPICS.pending }}</span>
            <span v-else class="tabular" :class="quietZero(row.documents ?? 0)">{{ formatNumber(row.documents ?? 0) }}</span>
          </template>
          <template #cell-actions="{ row }">
            <MenuButton
              v-if="row.rules?.length" size="sm" :label="TOPICS.rowMenu(row.tag)" :items="[{ key: 'remove', label: TOPICS.remove, icon: 'delete', danger: true }]"
              :data-test="`topic-menu-${row.tag}`" @select="remove(row)"
            />
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
            <UiButton variant="primary" icon="add" :disabled="!picksValid || publication.planning.value" data-test="add-topics" @click="addPicked">
              {{ TOPICS.add(newTopics) }}
            </UiButton>
          </div>
        </div>
      </AsyncState>
      <p v-if="publication.planning.value" class="m-0 text-small text-fg-2" role="status" data-test="topics-planning">{{ TOPICS.planning }}</p>
      <InlineError v-if="publication.error.value" :message="publication.error.value" data-test="topics-save-error" />
      <TopicRuleForm v-model:open="formOpen" :collection-id="collectionId" :sources="props.sources ?? []" :initial="editing" @save="saveForm" />
      <ConfirmDialog
        v-model:open="publication.confirming.value" :title="publication.title.value" :confirm-label="TOPICS.saveConfirm" :danger="removing" :action="confirm"
      >
        <div class="grid gap-3" data-test="topics-plan">
          <p class="m-0">{{ removing ? TOPICS.removeLead : TOPICS.saveLead }}</p>
          <ul class="m-0 grid gap-2 pl-5">
            <li v-for="p in publication.planned.value" :key="p.id" data-test="topics-plan-item">
              <span class="block">{{ TOPICS.savePlan(p.processing, formatNumber(p.publication?.plan?.documents ?? 0)) }}</span>
              <span class="block text-small text-fg-2">{{ TOPICS.saveSources(publication.affected(p)) }}</span>
            </li>
          </ul>
        </div>
      </ConfirmDialog>
    </template>
  </div>
</template>
