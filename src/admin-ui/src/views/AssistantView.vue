<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Assistant, AssistantDraft, AssistantTestResult, ClientSummary, CollectionSummary, GatewayModel } from '@/api/types'
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
import { ASSISTANT, LEVEL_LABEL } from '@/utils/labels'
import { ERROR_MESSAGES, formatMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'

// Assistent (admin design §5.5.4, §5.4 steps 9–12; #278 slice 3): the draft (model, instructions, collections) with the
// V-rule warnings, Testa on the draft (POST /api/assistants/{id}/test, nothing stored), Publicera (V34, V8 at the gateway),
// and Målgrupp (the clients that offer it).
type Tab = 'draft' | 'test' | 'audience'
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
const linked = ref<string[]>([])
const audience = ref<string[]>([])
const question = ref('')
const result = ref<AssistantTestResult | null>(null)
const busy = ref<'' | 'save' | 'publish' | 'test' | 'audience'>('')
const error = ref<unknown>(null)
const canEdit = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
const canManage = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin']))

const tabs = [
  { value: 'draft' as const, label: ASSISTANT.tabs.draft },
  { value: 'test' as const, label: ASSISTANT.tabs.test },
  { value: 'audience' as const, label: ASSISTANT.tabs.audience },
]
const modelOptions = computed(() => [{ value: '', label: ASSISTANT.noModel }, ...models.value.filter((m) => m.kind !== 'embedding').map((m) => ({ value: m.name, label: m.name }))])
const departmentCollections = computed(() => collections.value.filter((c) => c.departmentId === assistant.data.value?.departmentId))
const warnings = computed(() =>
  (draft.value?.warnings ?? []).map((w) => formatMessage(ERROR_MESSAGES[w.code] ?? w.code, w.params ?? undefined) + (w.affected.length ? ` (${w.affected.join(', ')})` : '')),
)

function fill(d: AssistantDraft | null): void {
  draft.value = d
  model.value = d?.gatewayModel ?? ''
  prompt.value = d?.systemPrompt ?? ''
  linked.value = [...(d?.collectionIds ?? [])]
}

function toggle(list: string[], id: string, on: boolean): string[] {
  return on ? [...new Set([...list, id])] : list.filter((x) => x !== id)
}

async function load(): Promise<void> {
  const a = await assistant.run(() => api.assistants.get(props.id))
  if (!a) return
  audience.value = [...a.audienceClientIds]
  fill(a.draft ? await api.assistants.draft(props.id).catch(() => null) : null)
  ;[models.value, collections.value, clients.value] = await Promise.all([
    api.gateway.models().catch(() => []),
    api.collections.list({ pageSize: 200 }).then((p) => p.items).catch(() => []),
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
  fill(await api.assistants.saveDraft(props.id, { gatewayModel: model.value || null, systemPrompt: prompt.value, collectionIds: linked.value, version: draft.value?.version ?? null }))
  assistant.data.value = await api.assistants.get(props.id)
  ui.notify(ASSISTANT.draftSaved)
})

const publish = () => run('publish', async () => {
  const a = assistant.data.value!
  const baseChanged = a.draft?.baseChanged ?? false
  if (baseChanged && !window.confirm(ASSISTANT.baseChanged)) return
  assistant.data.value = await api.assistants.publish(props.id, { version: a.version, draftVersion: a.draft!.version, confirmBaseChanged: baseChanged })
  fill(null)
  ui.notify(ASSISTANT.published(assistant.data.value.publishedVersion ?? 0))
})

const test = () => run('test', async () => {
  result.value = await api.assistants.test(props.id, { question: question.value.trim() })
})

const saveAudience = () => run('audience', async () => {
  assistant.data.value = await api.assistants.audiences(props.id, { clientIds: audience.value, version: assistant.data.value!.version })
  ui.notify(ASSISTANT.audienceSaved)
})

onMounted(load)
</script>

<template>
  <AsyncState :loading="assistant.loading.value && !assistant.data.value" :error="assistant.error.value" @retry="load">
    <template v-if="assistant.data.value">
      <PageHeader :title="assistant.data.value.name" :description="ASSISTANT.modelName(assistant.data.value.slug)">
        <UiBadge :label="LEVEL_LABEL[assistant.data.value.classification]" />
        <UiBadge v-if="assistant.data.value.publishedVersion" tone="ok" :label="ASSISTANT.publishedVersion(assistant.data.value.publishedVersion)" data-test="published" />
        <UiButton
          v-if="canEdit && assistant.data.value.draft" variant="primary" :loading="busy === 'publish'" data-test="publish" @click="publish"
        >
          {{ ASSISTANT.publish }}
        </UiButton>
      </PageHeader>
      <InlineError v-if="error" :message="problemMessage(error)" class="mb-4" />
      <UiTabs id="assistant" v-model="tab" :tabs="tabs" :label="ASSISTANT.tabsLabel" class="mb-6" />

      <div v-if="tab === 'draft'" id="assistant-panel-draft" role="tabpanel" aria-labelledby="assistant-tab-draft" class="grid max-w-3xl gap-5">
        <UiCard v-if="warnings.length" :title="ASSISTANT.warnings" data-test="warnings">
          <ul class="ml-5 list-disc text-warn"><li v-for="w in warnings" :key="w">{{ w }}</li></ul>
        </UiCard>
        <SelectField id="assistant-model" v-model="model" :label="ASSISTANT.model" :hint="ASSISTANT.modelHint" :options="modelOptions" />
        <TextAreaField id="assistant-prompt" v-model="prompt" :label="ASSISTANT.prompt" :hint="ASSISTANT.promptHint" :rows="8" />
        <fieldset class="grid gap-2">
          <legend class="mb-1 font-medium">{{ ASSISTANT.collections }}</legend>
          <p class="text-small text-fg-2">{{ ASSISTANT.collectionsHint }}</p>
          <CheckField
            v-for="c in departmentCollections" :key="c.id" :model-value="linked.includes(c.id)"
            :label="c.publishedAt ? c.name : `${c.name} (${ASSISTANT.notPublished})`" @update:model-value="linked = toggle(linked, c.id, $event)"
          />
        </fieldset>
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

      <div v-else id="assistant-panel-audience" role="tabpanel" aria-labelledby="assistant-tab-audience" class="grid max-w-3xl gap-4">
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
      </div>
    </template>
  </AsyncState>
</template>
