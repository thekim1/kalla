<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type {
  Assistant, AssistantDraft, AssistantTestResult, AssistantVersion, AssistantVersionSummary, ClientSummary, CollectionLink, CollectionSummary, DataProtectionPolicy,
  GatewayModel, GeneralKnowledge,
} from '@/api/types'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import TextField from '@/components/ui/TextField.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import ExplainAccess from '@/components/assistants/ExplainAccess.vue'
import KnowledgeLinks from '@/components/assistants/KnowledgeLinks.vue'
import { formatDateTime } from '@/utils/format'
import { ASSISTANT, COMMON, LEVEL_LABEL } from '@/utils/labels'
import { ERROR_MESSAGES, formatMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'

// Assistent (admin design §5.5.4, §5.4 steps 9–12; #278 slice 3): the draft (model, instructions, collections) with the
// V-rule warnings, Testa on the draft (POST /api/assistants/{id}/test, nothing stored), Publicera (V34, V8 at the gateway),
// Målgrupp (the clients that offer it), Versioner with Återställ (publishes an earlier version again as a new one), Lagring
// (history mode and retention days within the level's policy, V10) and Arkivera / Ta fram igen.
type Tab = 'draft' | 'test' | 'versions' | 'audience' | 'storage'
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const assistant = useLoadable<Assistant | null>(null)
const tab = ref<Tab>('draft')
const draft = ref<AssistantDraft | null>(null)
const models = ref<GatewayModel[]>([])
const collections = ref<CollectionSummary[]>([])
const clients = ref<ClientSummary[]>([])
const model = ref('')
const prompt = ref('')
// The knowledge links with their topics (admin design §5.5.4): own and shared knowledge bases.
const links = ref<CollectionLink[]>([])
// The versioned query steps and not-found texts are kept as loaded and sent back whole; the form edits only answer.generalKnowledge.
const querySteps = ref<Record<string, unknown>>({})
const notFound = ref<Record<string, unknown>>({})
const generalKnowledge = ref<GeneralKnowledge>('off')
// The query settings under "Advanced" (admin design §5.5.4; chat design §5–§6, V17); empty = the key is left out.
const advanced = ref({ topK: '', rerank: '', condense: true, maxMessages: '', maxOutputTokens: '', temperature: '', language: '' })
const advancedErrors = ref<Record<string, string | undefined>>({})
const LIMITS = { topK: [1, 50], maxMessages: [0, 20], maxOutputTokens: [64, 4096], temperature: [0, 1] } as const
const languageOptions = ['', 'auto', 'sv'].map((v) => ({ value: v, label: ASSISTANT.languages[v]! }))
const GENERAL_KNOWLEDGE_LEVELS: GeneralKnowledge[] = ['off', 'supplement', 'fallback', 'open']
const audience = ref<string[]>([])
const question = ref('')
const result = ref<AssistantTestResult | null>(null)
const busy = ref<'' | 'save' | 'publish' | 'test' | 'audience' | 'storage' | 'unarchive'>('')
const versions = ref<AssistantVersionSummary[] | null>(null)
const restoring = ref<number | null>(null)
const archiving = ref(false)
const policy = ref<DataProtectionPolicy | null>(null)
const historyMode = ref('none')
const retentionDays = ref('90')
const retentionError = ref<string | undefined>(undefined)
const error = ref<unknown>(null)
const canEdit = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
const canManage = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin']))

const tabs = [
  { value: 'draft' as const, label: ASSISTANT.tabs.draft },
  { value: 'test' as const, label: ASSISTANT.tabs.test },
  { value: 'versions' as const, label: ASSISTANT.tabs.versions },
  { value: 'audience' as const, label: ASSISTANT.tabs.audience },
  { value: 'storage' as const, label: ASSISTANT.tabs.storage },
]
const versionColumns: Column[] = [
  { key: 'number', label: ASSISTANT.version },
  { key: 'state', label: ASSISTANT.versionState },
  { key: 'by', label: ASSISTANT.versionBy },
  { key: 'when', label: ASSISTANT.versionWhen },
  { key: 'actions', label: ASSISTANT.restore, hideLabel: true, align: 'right' },
]
const archived = computed(() => assistant.data.value?.state === 'archived')
// V36 (#445): general knowledge on a public assistant shows a warning and needs one extra confirmation to publish.
const publicLevel = computed(() => assistant.data.value?.classification === 'public')
const historyOptions = computed(() =>
  (policy.value?.allowedHistoryModes ?? [assistant.data.value?.historyMode ?? 'none']).map((m) => ({ value: m, label: ASSISTANT.historyModes[m] ?? m })),
)
const restorable = (v: AssistantVersionSummary) => v.state === 'superseded'
// Rerank is optional (B-16): a reranker can be chosen only while the platform has a model for it; with none the choice is
// disabled with a hint. A stored choice without a model stays visible and selected (answers then use the plain ranking).
const rerankers = computed<string[]>(() => assistant.data.value?.rerankers ?? [])
const rerankOptions = computed(() =>
  ['', 'chat', 'gateway'].map((v) => ({ value: v, label: ASSISTANT.rerankOptions[v]!, disabled: v !== '' && v !== advanced.value.rerank && !rerankers.value.includes(v) })),
)
const rerankHint = computed(() => {
  if (advanced.value.rerank && !rerankers.value.includes(advanced.value.rerank)) return ASSISTANT.rerankMissing
  return rerankers.value.length === 0 ? ASSISTANT.rerankNone : ASSISTANT.rerankHint
})
const rerankOf = (v: unknown): string => (v === 'chat' || v === 'gateway' ? v : '')
const modelOptions = computed(() => [{ value: '', label: ASSISTANT.noModel }, ...models.value.filter((m) => m.modelKind === 'chat').map((m) => ({ value: m.name, label: m.name }))])
/** §5.4 step 13: once published, a link to each audience client's own UI (`PortalUrl`), where the model has its slug. */
const portals = computed(() =>
  assistant.data.value?.publishedVersion && !archived.value
    ? clients.value.filter((c) => c.portalUrl && assistant.data.value!.audienceClientIds.includes(c.id))
    : [],
)
/** "Who can use it" (admin design §5.5.4): the saved audience in plain words. */
const whoCanUse = computed(() =>
  clients.value.filter((c) => assistant.data.value?.audienceClientIds.includes(c.id)).map((c) => (ASSISTANT.whoByKind[c.kind] ?? ASSISTANT.whoFallback)(c.name)),
)
const warnings = computed(() =>
  (draft.value?.warnings ?? []).map((w) => formatMessage(ERROR_MESSAGES[w.code] ?? w.code, w.params ?? undefined) + (w.affected.length ? ` (${w.affected.join(', ')})` : '')),
)

type Settings = Pick<AssistantVersion, 'gatewayModel' | 'systemPrompt' | 'collectionLinks' | 'querySteps' | 'notFound'>

function levelOf(steps: Record<string, unknown> | undefined): GeneralKnowledge {
  const value = (steps?.answer as Record<string, unknown> | undefined)?.generalKnowledge
  return GENERAL_KNOWLEDGE_LEVELS.includes(value as GeneralKnowledge) ? (value as GeneralKnowledge) : 'off'
}

function fill(d: AssistantDraft | null, base: Settings | null = null): void {
  draft.value = d
  const s = d ?? base
  model.value = s?.gatewayModel ?? ''
  prompt.value = s?.systemPrompt ?? ''
  links.value = (s?.collectionLinks ?? []).map((l) => ({ collectionId: l.collectionId, tags: [...l.tags] }))
  querySteps.value = { ...(s?.querySteps ?? {}) }
  notFound.value = { ...(s?.notFound ?? {}) }
  generalKnowledge.value = levelOf(s?.querySteps)
  const steps = (s?.querySteps ?? {}) as Record<string, Record<string, unknown> | undefined>
  const text = (v: unknown) => (typeof v === 'number' ? String(v) : '')
  advanced.value = {
    topK: text(steps.retrieval?.topK),
    rerank: rerankOf(steps.retrieval?.rerank), condense: steps.condense?.enabled !== false,
    maxMessages: text(steps.history?.maxMessages),
    maxOutputTokens: text(steps.answer?.maxOutputTokens), temperature: text(steps.answer?.temperature),
    language: typeof steps.answer?.language === 'string' ? steps.answer.language : '',
  }
  advancedErrors.value = {}
}

/** Checks the Advanced fields; true when every one is empty or in range. */
function checkAdvanced(): boolean {
  const errors: Record<string, string | undefined> = {}
  for (const [key, [min, max]] of Object.entries(LIMITS) as [keyof typeof LIMITS, readonly [number, number]][]) {
    const raw = advanced.value[key].trim().replace(',', '.')
    const value = Number(raw)
    const whole = key !== 'temperature'
    if (raw && (Number.isNaN(value) || value < min || value > max || (whole && !Number.isInteger(value)))) errors[key] = ASSISTANT.rangeInvalid(min, max)
  }
  advancedErrors.value = errors
  return Object.values(errors).every((e) => !e)
}

function stepsToSave(): Record<string, unknown> {
  const part = (name: string) => ({ ...((querySteps.value[name] as Record<string, unknown> | undefined) ?? {}) })
  const set = (target: Record<string, unknown>, key: string, raw: string) => {
    const text = raw.trim().replace(',', '.')
    if (text) target[key] = Number(text)
    else delete target[key]
  }
  const retrieval = part('retrieval')
  const history = part('history')
  const answer: Record<string, unknown> = { ...part('answer'), generalKnowledge: generalKnowledge.value }
  const condense = { ...part('condense'), enabled: advanced.value.condense }
  set(retrieval, 'topK', advanced.value.topK)
  // Rerank is off unless a reranker is chosen (Decisions "Rerank pulled forward", B-16); only an explicit choice is stored.
  if (advanced.value.rerank) retrieval.rerank = advanced.value.rerank
  else delete retrieval.rerank
  set(history, 'maxMessages', advanced.value.maxMessages)
  set(answer, 'maxOutputTokens', advanced.value.maxOutputTokens)
  set(answer, 'temperature', advanced.value.temperature)
  if (advanced.value.language) answer.language = advanced.value.language
  else delete answer.language
  const steps: Record<string, unknown> = { ...querySteps.value, answer }
  for (const [name, value] of [['retrieval', retrieval], ['history', history]] as const) {
    if (Object.keys(value).length) steps[name] = value
    else delete steps[name]
  }
  // Follow-up rewriting is on unless turned off (chat design §5); only an explicit choice is stored.
  if (advanced.value.condense && !('condense' in querySteps.value)) delete steps.condense
  else steps.condense = condense
  return steps
}

async function fillFrom(a: Assistant): Promise<void> {
  if (a.draft) fill(await api.assistants.draft(props.id).catch(() => null))
  else fill(null, a.publishedVersion ? await api.assistants.version(props.id, a.publishedVersion).catch(() => null) : null)
}

function toggle(list: string[], id: string, on: boolean): string[] {
  return on ? [...new Set([...list, id])] : list.filter((x) => x !== id)
}

async function load(): Promise<void> {
  const a = await assistant.run(() => api.assistants.get(props.id))
  if (!a) return
  audience.value = [...a.audienceClientIds]
  historyMode.value = a.historyMode
  retentionDays.value = String(a.retentionDays)
  await fillFrom(a)
  ;[models.value, collections.value, clients.value] = await Promise.all([
    api.gateway.models().catch(() => []),
    api.collections.list({ pageSize: 200, view: 'all' }).then((p) => p.items).catch(() => []),
    api.clients.list().catch(() => []),
  ])
}

async function run(kind: typeof busy.value, action: () => Promise<void>): Promise<void> {
  busy.value = kind
  error.value = null
  try {
    await action()
  } catch (e) {
    error.value = e
  } finally {
    busy.value = ''
  }
}

const saveDraft = () => run('save', async () => {
  if (!checkAdvanced()) return
  fill(await api.assistants.saveDraft(props.id, {
    gatewayModel: model.value || null, systemPrompt: prompt.value, collectionLinks: links.value, querySteps: stepsToSave(), notFound: notFound.value,
    version: draft.value?.version ?? null,
  }))
  assistant.data.value = await api.assistants.get(props.id)
  ui.notify(ASSISTANT.draftSaved)
})

/**
 * V36 (#445): general knowledge on a public assistant needs an explicit confirmation. The API answers 409
 * general_knowledge_public_unconfirmed; ask once and resend with the flag (false: the user declined).
 */
async function withGeneralKnowledgeConfirmation<T>(send: (confirm: boolean) => Promise<T>): Promise<T | null> {
  try {
    return await send(false)
  } catch (e) {
    if (!(e instanceof ApiError) || e.problem?.code !== 'general_knowledge_public_unconfirmed') throw e
    if (!window.confirm(ASSISTANT.generalKnowledgeConfirm)) return null
    return await send(true)
  }
}

const publish = () => run('publish', async () => {
  const a = assistant.data.value!
  const baseChanged = a.draft?.baseChanged ?? false
  if (baseChanged && !window.confirm(ASSISTANT.baseChanged)) return
  const published = await withGeneralKnowledgeConfirmation((confirmGeneralKnowledge) =>
    api.assistants.publish(props.id, { version: a.version, draftVersion: a.draft!.version, confirmBaseChanged: baseChanged, confirmGeneralKnowledge }))
  if (!published) return
  assistant.data.value = published
  fill(null, draft.value)
  if (versions.value) await loadVersions()
  ui.notify(ASSISTANT.published(assistant.data.value.publishedVersion ?? 0))
})

const test = () => run('test', async () => {
  result.value = await api.assistants.test(props.id, { question: question.value.trim() })
})

const saveAudience = () => run('audience', async () => {
  assistant.data.value = await api.assistants.audiences(props.id, { clientIds: audience.value, version: assistant.data.value!.version })
  ui.notify(ASSISTANT.audienceSaved)
})

async function loadVersions(): Promise<void> {
  versions.value = await api.assistants.versions(props.id).catch((e) => {
    error.value = e
    return []
  })
}

async function loadPolicy(): Promise<void> {
  if (policy.value || !assistant.data.value) return
  policy.value = await api.dataProtection.get(assistant.data.value.classification).catch(() => null)
}

watch(tab, (t) => {
  if (t === 'versions') void loadVersions()
  if (t === 'storage') void loadPolicy()
})

async function restore(): Promise<void> {
  const n = restoring.value!
  const version = assistant.data.value!.version
  const restored = await withGeneralKnowledgeConfirmation((confirm) => api.assistants.restore(props.id, n, version, confirm))
  if (!restored) return
  assistant.data.value = restored
  if (!assistant.data.value.draft) await fillFrom(assistant.data.value)
  ui.notify(ASSISTANT.restored(assistant.data.value.publishedVersion ?? n))
  await loadVersions()
}

async function archive(): Promise<void> {
  assistant.data.value = await api.assistants.archive(props.id, assistant.data.value!.version)
  ui.notify(ASSISTANT.archivedDone)
}

const unarchive = () => run('unarchive', async () => {
  assistant.data.value = await api.assistants.unarchive(props.id, assistant.data.value!.version)
  ui.notify(ASSISTANT.unarchivedDone)
})

const saveStorage = () => run('storage', async () => {
  const max = policy.value?.retentionMaxDays ?? 3650
  const days = Number(retentionDays.value.trim())
  retentionError.value = /^\d+$/.test(retentionDays.value.trim()) && days >= 1 && days <= max ? undefined : ASSISTANT.retentionInvalid(max)
  if (retentionError.value) return
  assistant.data.value = await api.assistants.retention(props.id, { historyMode: historyMode.value, retentionDays: days, version: assistant.data.value!.version })
  ui.notify(ASSISTANT.storageSaved)
})

onMounted(load)
</script>

<template>
  <AsyncState :loading="assistant.loading.value && !assistant.data.value" :error="assistant.error.value" @retry="load">
    <template v-if="assistant.data.value">
      <PageHeader :title="assistant.data.value.name" :description="ASSISTANT.modelName(assistant.data.value.slug)">
        <UiBadge :label="LEVEL_LABEL[assistant.data.value.classification]" />
        <UiBadge v-if="assistant.data.value.publishedVersion" tone="ok" :label="ASSISTANT.publishedVersion(assistant.data.value.publishedVersion)" data-test="published" />
        <UiBadge v-if="archived" tone="warn" :label="ASSISTANT.archived" data-test="archived" />
        <UiBadge
          v-if="assistant.data.value.generalKnowledge && assistant.data.value.generalKnowledge !== 'off'" :label="ASSISTANT.generalKnowledgeBadge"
          data-test="general-knowledge-badge"
        />
        <UiButton v-for="c in portals" :key="c.id" :href="c.portalUrl!" target="_blank" rel="noopener noreferrer" icon="open_in_new" data-test="portal">
          {{ ASSISTANT.openIn(c.name) }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span>
        </UiButton>
        <template v-if="canEdit">
          <UiButton v-if="archived" :loading="busy === 'unarchive'" data-test="unarchive" @click="unarchive">{{ ASSISTANT.unarchive }}</UiButton>
          <UiButton v-else icon="archive" data-test="archive" @click="archiving = true">{{ ASSISTANT.archive }}</UiButton>
        </template>
        <UiButton
          v-if="canEdit && assistant.data.value.draft" variant="primary" :loading="busy === 'publish'" data-test="publish" @click="publish"
        >
          {{ ASSISTANT.publish }}
        </UiButton>
      </PageHeader>
      <InlineError v-if="error" :message="problemMessage(error)" class="mb-4" />
      <UiTabs id="assistant" v-model="tab" :tabs="tabs" :label="ASSISTANT.tabsLabel" class="mb-6" />

      <div v-if="tab === 'draft'" id="assistant-panel-draft" role="tabpanel" aria-labelledby="assistant-tab-draft" class="grid max-w-3xl gap-5">
        <p v-if="!assistant.data.value.draft && assistant.data.value.publishedVersion" class="text-fg-2" data-test="draft-from">
          {{ ASSISTANT.draftFrom(assistant.data.value.publishedVersion) }}
        </p>
        <UiCard v-if="warnings.length" :title="ASSISTANT.warnings" data-test="warnings">
          <ul class="ml-5 list-disc text-warn"><li v-for="w in warnings" :key="w">{{ w }}</li></ul>
        </UiCard>
        <TextAreaField id="assistant-prompt" v-model="prompt" :label="ASSISTANT.prompt" :hint="ASSISTANT.promptHint" :rows="8" />
        <KnowledgeLinks v-model="links" :collections="collections" :department-id="assistant.data.value.departmentId" :disabled="!canEdit" />
        <fieldset class="grid gap-2" data-test="general-knowledge" aria-describedby="assistant-general-knowledge-hint">
          <legend class="mb-1 font-medium">{{ ASSISTANT.generalKnowledge }}</legend>
          <p id="assistant-general-knowledge-hint" class="text-small text-fg-2">{{ ASSISTANT.generalKnowledgeHint }}</p>
          <label v-for="g in GENERAL_KNOWLEDGE_LEVELS" :key="g" class="flex cursor-pointer gap-3 rounded-card border border-border p-3">
            <input
              :id="`assistant-general-knowledge-${g}`" v-model="generalKnowledge" type="radio" name="assistant-general-knowledge" :value="g" class="mt-1"
            />
            <span>
              <span class="block font-medium">{{ ASSISTANT.generalKnowledgeLevels[g]!.label }}</span>
              <span class="block text-small text-fg-2">{{ ASSISTANT.generalKnowledgeLevels[g]!.help }}</span>
            </span>
          </label>
          <p v-if="publicLevel && generalKnowledge !== 'off'" class="text-small text-warn" role="status" data-test="general-knowledge-public-warning">
            {{ ASSISTANT.generalKnowledgePublicWarning }}
          </p>
        </fieldset>
        <details class="rounded-card border border-border p-4" data-test="advanced">
          <summary class="cursor-pointer font-medium">{{ ASSISTANT.advanced }}</summary>
          <div class="mt-4 grid gap-5">
            <p class="text-small text-fg-2">{{ ASSISTANT.advancedHint }}</p>
            <SelectField id="assistant-model" v-model="model" :label="ASSISTANT.model" :hint="ASSISTANT.modelHint" :options="modelOptions" />
            <TextField id="assistant-top-k" v-model="advanced.topK" :label="ASSISTANT.topK" inputmode="numeric" :error="advancedErrors.topK" />
            <SelectField
              id="assistant-rerank" v-model="advanced.rerank" :label="ASSISTANT.rerank" :hint="rerankHint" :options="rerankOptions"
              :disabled="rerankers.length === 0 && !advanced.rerank" data-test="rerank"
            />
            <CheckField v-model="advanced.condense" :label="ASSISTANT.condense" data-test="condense" />
            <TextField id="assistant-history" v-model="advanced.maxMessages" :label="ASSISTANT.historyMessages" inputmode="numeric" :error="advancedErrors.maxMessages" />
            <TextField id="assistant-max-tokens" v-model="advanced.maxOutputTokens" :label="ASSISTANT.maxOutputTokens" inputmode="numeric" :error="advancedErrors.maxOutputTokens" />
            <TextField id="assistant-temperature" v-model="advanced.temperature" :label="ASSISTANT.temperature" inputmode="decimal" :error="advancedErrors.temperature" />
            <SelectField id="assistant-language" v-model="advanced.language" :label="ASSISTANT.language" :options="languageOptions" />
          </div>
        </details>
        <div v-if="canEdit"><UiButton variant="primary" :loading="busy === 'save'" data-test="save-draft" @click="saveDraft">{{ ASSISTANT.saveDraft }}</UiButton></div>
      </div>

      <div v-else-if="tab === 'test'" id="assistant-panel-test" role="tabpanel" aria-labelledby="assistant-tab-test" class="grid max-w-3xl gap-5">
        <form class="grid gap-3" @submit.prevent="test">
          <TextAreaField id="assistant-question" v-model="question" :label="ASSISTANT.question" :hint="ASSISTANT.askHint" :rows="2" />
          <div><UiButton type="submit" variant="primary" :loading="busy === 'test'" :disabled="!question.trim()" data-test="ask">{{ ASSISTANT.ask }}</UiButton></div>
        </form>
        <template v-if="result">
          <UiCard :title="ASSISTANT.answer" data-test="answer">
            <UiBadge :label="ASSISTANT.outcome[result.answer.outcome.replace('-', '')] ?? result.answer.outcome" class="mb-2" />
            <p class="whitespace-pre-wrap">{{ result.answer.delivered }}</p>
            <details v-if="result.answer.raw" class="mt-3"><summary class="cursor-pointer text-small">{{ ASSISTANT.raw }}</summary><p class="whitespace-pre-wrap text-small text-fg-2">{{ result.answer.raw }}</p></details>
            <p class="mt-3 text-small text-fg-3">{{ ASSISTANT.gateway(result.gateway.model, result.gateway.residency, String(Math.round(result.durationMs))) }}</p>
          </UiCard>
          <UiCard :title="ASSISTANT.sources">
            <ol class="grid gap-3">
              <li v-for="c in result.retrieval.candidates" :key="c.rank">
                <span class="font-medium">{{ c.rank }}. {{ c.title }}</span>
                <UiBadge v-if="c.cited" tone="ok" :label="ASSISTANT.cited" class="ml-2" />
                <span class="block text-small text-fg-3">{{ c.headingPath }}</span>
                <span class="block text-small text-fg-2">{{ c.text }}</span>
              </li>
            </ol>
          </UiCard>
        </template>
      </div>

      <div v-else-if="tab === 'versions'" id="assistant-panel-versions" role="tabpanel" aria-labelledby="assistant-tab-versions" class="grid max-w-4xl gap-4">
        <p class="text-fg-2">{{ ASSISTANT.versionsLead }}</p>
        <DataTable
          v-if="versions" :columns="versionColumns" :rows="versions" :row-key="(v) => String(v.number)" :caption="ASSISTANT.tabs.versions"
          :empty-text="ASSISTANT.noVersions" data-test="versions"
        >
          <template #cell-number="{ row }">
            <span class="font-medium">{{ row.number }}</span>
            <span v-if="row.restoredFrom" class="block text-small text-fg-3">{{ ASSISTANT.restoredFrom(row.restoredFrom) }}</span>
          </template>
          <template #cell-state="{ row }">
            <UiBadge :tone="row.state === 'published' ? 'ok' : 'neutral'" :label="ASSISTANT.versionStates[row.state] ?? row.state" />
          </template>
          <template #cell-by="{ row }">{{ row.publishedBy ?? row.createdBy ?? '' }}</template>
          <template #cell-when="{ row }"><span class="tabular text-fg-2">{{ formatDateTime(row.publishedAt ?? row.createdAt) }}</span></template>
          <template #cell-actions="{ row }">
            <UiButton v-if="canEdit && restorable(row as AssistantVersionSummary)" size="sm" :data-test="`restore-${row.number}`" @click="restoring = row.number">
              {{ ASSISTANT.restore }}
            </UiButton>
          </template>
        </DataTable>
      </div>

      <div v-else-if="tab === 'storage'" id="assistant-panel-storage" role="tabpanel" aria-labelledby="assistant-tab-storage" class="grid max-w-3xl gap-5">
        <p class="text-fg-2">{{ ASSISTANT.storageLead }}</p>
        <SelectField id="assistant-history" v-model="historyMode" :label="ASSISTANT.historyMode" :options="historyOptions" :disabled="!canManage" />
        <TextField
          id="assistant-retention" v-model="retentionDays" :label="ASSISTANT.retentionDays" inputmode="numeric" :disabled="!canManage"
          :hint="policy ? ASSISTANT.retentionHint(policy.retentionMaxDays) : undefined" :error="retentionError"
        />
        <div v-if="canManage"><UiButton variant="primary" :loading="busy === 'storage'" data-test="save-storage" @click="saveStorage">{{ ASSISTANT.saveStorage }}</UiButton></div>
      </div>

      <div v-else id="assistant-panel-audience" role="tabpanel" aria-labelledby="assistant-tab-audience" class="grid max-w-3xl gap-4">
        <section class="grid gap-1" data-test="who-can-use" aria-labelledby="assistant-who-can-use">
          <h3 id="assistant-who-can-use" class="font-medium">{{ ASSISTANT.whoCanUse }}</h3>
          <p v-if="whoCanUse.length === 0" class="text-fg-3">{{ ASSISTANT.whoCanUseNone }}</p>
          <ul v-else class="ml-5 list-disc"><li v-for="w in whoCanUse" :key="w">{{ w }}</li></ul>
        </section>
        <p class="text-fg-2">{{ ASSISTANT.audienceHint }}</p>
        <p v-if="clients.length === 0" class="text-fg-3">{{ ASSISTANT.noClients }}</p>
        <fieldset v-else class="grid gap-2">
          <legend class="mb-1 font-medium">{{ ASSISTANT.audience }}</legend>
          <CheckField
            v-for="c in clients" :key="c.id" :model-value="audience.includes(c.id)" :label="c.name" :disabled="!canManage"
            @update:model-value="audience = toggle(audience, c.id, $event)"
          />
        </fieldset>
        <div v-if="canManage && clients.length"><UiButton variant="primary" :loading="busy === 'audience'" data-test="save-audience" @click="saveAudience">{{ ASSISTANT.saveAudience }}</UiButton></div>
        <ExplainAccess v-if="canManage && clients.length" :assistant-id="id" :clients="clients" class="mt-4" />
      </div>
      <ConfirmDialog
        :open="restoring !== null" :title="ASSISTANT.restoreTitle(restoring ?? 0)" :consequence="ASSISTANT.restoreConsequence(restoring ?? 0)"
        :confirm-label="ASSISTANT.restore" :action="restore" @update:open="!$event && (restoring = null)"
      />
      <ConfirmDialog
        v-model:open="archiving" :title="ASSISTANT.archiveTitle" :consequence="ASSISTANT.archiveConsequence" :confirm-label="ASSISTANT.archive" danger
        :action="archive"
      />
    </template>
  </AsyncState>
</template>
