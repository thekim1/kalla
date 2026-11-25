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
import { addRules, findRule, normaliseTag, removeTopic, replaceRule, ruleForm, ruleInWords, tagError, tagsOf, topicSources } from '@/utils/topics'
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
/** One topic per tag: two sections on the same tag are one topic with two rules; a tag the list has already isn't new. */
const newTopics = computed(() => {
  const known = new Set(topics.data.value.map((t) => t.tag))
  return new Set(pickList.value.map((p) => normaliseTag(p.tag)).filter((t) => !known.has(t))).size
})
/** While a change is planned or waits for its Confirm, no second one starts (#1076: it would be lost). */
const busy = computed(() => publication.planning.value || publication.confirming.value)
const picksValid = computed(() => pickList.value.length > 0 && pickList.value.every((p) => tagError(p.tag) === null))

/** The hosts of each picked website section (path → hosts), for its host-first rules. */
const pickHosts: Record<string, Record<string, string[]>> = {}

function pick(sourceId: string, path: string, tag: string | null, hosts?: string[]): void {
  const paths = (picks[sourceId] ??= {})
  const known = (pickHosts[sourceId] ??= {})
  if (tag === null) {
    delete paths[path]
    delete known[path]
  } else {
    paths[path] = tag
    if (hosts) known[path] = hosts
  }
}

/**
 * A picked section's rules (Decisions "Topics editor", #1082): on a website one host-first rule per host the section was
 * found on (`www.umea.se/omsorg/`), so a host the source gains later is not tagged by it; an upload's folder as it is.
 */
function sectionRules(sourceId: string, path: string, tag: string): PathRuleJson[] {
  const hosts = pickHosts[sourceId]?.[path] ?? []
  return (hosts.length ? hosts.map((host) => `${host}${path}`) : [path]).map((match) => ({ match, on: 'path' as const, tags: [tag] }))
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
    changes.set(p.sourceId, [...(changes.get(p.sourceId) ?? []), addRules(sectionRules(p.sourceId, p.path, normaliseTag(p.tag)))])
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

/** A refused plan keeps the form open with why (#1076), so what was written isn't lost. */
const formError = ref<string | null>(null)

function addTopic(): void {
  formError.value = null
  editing.value = null
  formOpen.value = true
}

async function editRule(topic: Topic, rule: { sourceId: string; on: string; match: string }): Promise<void> {
  formError.value = null
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

async function saveForm({ sourceId, rule }: { sourceId: string; rule: PathRuleJson }): Promise<void> {
  const changes = new Map<string, RuleChange[]>()
  const was = editing.value
  const tag = tagsOf(rule).map(normaliseTag)[0] ?? ''
  removing.value = false
  formError.value = null
  if (!was) {
    changes.set(sourceId, [addRules([rule])])
    doneMessage.value = TOPICS.saved(1)
    await publication.plan(changes, TOPICS.saveTitle(1))
  } else {
    // A rule edited as its JSON had every topic in view: it is replaced as a whole (#1076).
    const whole = ruleForm(was.rule) === 'json'
    const replace = replaceRule(was.tag, was.rule.on, was.rule.match, rule, whole)
    if (was.sourceId === sourceId) changes.set(sourceId, [replace])
    else {
      // Moved to another source: the old one loses this topic's rule, the new one gains it.
      changes.set(was.sourceId, [(rules) => replace(rules).filter((r) => r !== rule)])
      changes.set(sourceId, [addRules([rule])])
    }
    doneMessage.value = TOPICS.changed(tag)
    await publication.plan(changes, TOPICS.editTitle(tag))
  }
  if (publication.error.value) formError.value = publication.error.value
  else formOpen.value = false
}

const removing = ref(false)
function remove(topic: Topic): void {
  if (busy.value) return
  const changes = new Map<string, RuleChange[]>()
  for (const sourceId of new Set((topic.rules ?? []).map((r) => r.sourceId))) changes.set(sourceId, [removeTopic(topic.tag)])
  doneMessage.value = TOPICS.removed(topic.tag)
  removing.value = true
  void publication.plan(changes, TOPICS.removeTitle(topic.tag))
}

/** Every attempt reads the list and the suggestions again, so what is published shows also after a failure (#1075). */
async function confirm(): Promise<void> {
  try {
    await publication.confirm()
    for (const sourceId of Object.keys(picks)) delete picks[sourceId]
    ui.notify(doneMessage.value)
  } finally {
    load()
  }
}

onMounted(load)
</script>

<template>
  <div class="grid gap-6">
    <div class="grid gap-4">
      <div class="flex flex-wrap items-start justify-between gap-3">
        <p class="m-0 max-w-[680px] text-fg-2">{{ TOPICS.lead }}</p>
        <UiButton v-if="canEditTopics" icon="add" :disabled="busy" data-test="add-topic" @click="addTopic">{{ TOPICS.addTopic }}</UiButton>
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
                  v-if="canEditTopics" type="button" class="inline-flex min-h-6 items-center text-small text-accent-ink underline max-md:min-h-11"
                  :aria-label="TOPICS.editRule(row.tag, ruleInWords(r))" :disabled="opening || busy" data-test="edit-rule" @click="editRule(row, r)"
                >{{ TOPICS.edit }}</button>
              </span>
            </span>
            <span v-else class="text-fg-3">{{ TOPICS.noRule }}</span>
          </template>
          <template #cell-source="{ row }">{{ topicSources(row).join(', ') }}</template>
          <template #cell-documents="{ row }">
            <span v-if="row.pending" class="text-fg-3" data-test="topic-pending">{{ TOPICS.pending }}</span>
            <!-- Removed, not yet processed again (#1082): its count, and in words that it is going. -->
            <span v-else-if="row.pendingRemoval" class="tabular" data-test="topic-pending-removal">{{ formatNumber(row.documents ?? 0) }} · {{ TOPICS.pendingRemoval }}</span>
            <span v-else class="tabular" :class="quietZero(row.documents ?? 0)">{{ formatNumber(row.documents ?? 0) }}</span>
          </template>
          <template #cell-actions="{ row }">
            <MenuButton
              v-if="row.rules?.length && !row.pendingRemoval" size="sm" :label="TOPICS.rowMenu(row.tag)" :items="[{ key: 'remove', label: TOPICS.remove, icon: 'delete', danger: true, disabled: busy }]"
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
            @pick="(path, tag, hosts) => pick(block.sourceId, path, tag, hosts)"
          />
          <div v-if="suggestions.data.value.some((b) => b.sections.length > 0)" class="grid justify-items-end gap-2">
            <UiButton variant="primary" icon="add" :disabled="!picksValid || busy" data-test="add-topics" @click="addPicked">
              {{ TOPICS.add(newTopics) }}
            </UiButton>
          </div>
        </div>
      </AsyncState>
      <!-- Always mounted, so the text is announced when it appears. -->
      <p class="m-0 text-small text-fg-2" role="status" data-test="topics-planning">{{ publication.planning.value ? TOPICS.planning : '' }}</p>
      <!-- A refusal while the form is open shows in the form only. -->
      <InlineError v-if="publication.error.value && !formOpen" :message="publication.error.value" data-test="topics-save-error" />
      <TopicRuleForm
        v-model:open="formOpen" :collection-id="collectionId" :sources="props.sources ?? []" :initial="editing" :saving="publication.planning.value"
        :save-error="formError" @save="saveForm"
      />
      <ConfirmDialog
        v-model:open="publication.confirming.value" :title="publication.title.value" :confirm-label="TOPICS.saveConfirm" :danger="removing" :action="confirm"
      >
        <div class="grid gap-3" data-test="topics-plan">
          <p class="m-0">{{ removing ? TOPICS.removeLead : TOPICS.saveLead }}</p>
          <ul class="m-0 grid gap-2 pl-5">
            <li v-for="p in publication.planned.value" :key="p.id" data-test="topics-plan-item">
              <span class="block">{{ TOPICS.savePlan(p.processing, formatNumber(p.publication?.plan?.documents ?? 0)) }}</span>
              <span class="block text-small text-fg-2">{{ TOPICS.saveSources(publication.affected(p)) }}</span>
              <span v-if="p.replacesDraft" class="block text-small text-warn" data-test="topics-plan-draft">{{ TOPICS.replacesDraft }}</span>
            </li>
          </ul>
        </div>
      </ConfirmDialog>
    </template>
  </div>
</template>
