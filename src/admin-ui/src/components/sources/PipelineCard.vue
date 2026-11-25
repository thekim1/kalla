<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { PathRuleJson, Pipeline, Source, TemplateDetail } from '@/api/types'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useAuthStore } from '@/stores/auth'
import { COMMON, PIPELINE } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { ruleInWords, tagsOf } from '@/utils/topics'

// Bearbetning on the source page (admin design §6.3, §6.6 step 1): the pipeline the source uses, its version and template
// parameters, and Ändra bearbetning: the parameters as a form from the template's paramsSchema (enum → select, integer →
// number with its range, anything else → JSON), then Publicera… plans the publication and opens the Publicering page.
// A department user on a platform pipeline publishes a copy for the collection (copy on write, #256).
const props = defineProps<{ source: Source }>()
const auth = useAuthStore()
const router = useRouter()
const pipeline = ref<Pipeline | null>(null)
const template = ref<TemplateDetail | null>(null)
const open = ref(false)
const planning = ref(false)
const error = ref<unknown>(null)
const values = reactive<Record<string, string>>({})
const fieldErrors = reactive<Record<string, string | undefined>>({})
const canPublish = computed(() => auth.can('edit-pipelines'))

const published = computed(() => pipeline.value?.versions.find((v) => v.id === pipeline.value?.publishedVersionId) ?? null)
const draft = computed(() => pipeline.value?.versions.find((v) => v.state === 'draft') ?? null)
/** The parameters the form starts from: the draft's when there is one, else the published version's. */
const current = computed(() => draft.value ?? published.value)
const copyOnWrite = computed(() => pipeline.value?.departmentId === null && !auth.isPlatformAdmin)
const fields = computed(() =>
  Object.entries(template.value?.paramsSchema.properties ?? {}).map(([name, schema]) => {
    const kind = Array.isArray(schema.enum) ? 'select' : schema.type === 'integer' ? 'integer' : 'json'
    const labels = schema['x-labels'] as { en?: string; sv?: string } | undefined
    return {
      name, kind, schema,
      label: PIPELINE.params[name] ?? labels?.en ?? labels?.sv ?? name,
      options: kind === 'select' ? (schema.enum as string[]).map((v) => ({ value: v, label: PIPELINE.options[v] ?? v })) : [],
      hint: kind === 'integer' && typeof schema.minimum === 'number' && typeof schema.maximum === 'number'
        ? PIPELINE.range(schema.minimum, schema.maximum) : kind === 'json' ? PIPELINE.json : undefined,
    }
  }),
)
/**
 * Topics (`pathRules`) are edited on the knowledge base's Topics tab (§5.5.2, §5.8; Decisions "Topics editor"): here they
 * are shown in words, read-only, and a publication of the other parameters sends their stored value unchanged.
 */
const editable = computed(() => fields.value.filter((f) => f.name !== 'pathRules'))
const hasPathRules = computed(() => fields.value.some((f) => f.name === 'pathRules'))
/**
 * The published topics, as the Topics tab shows them: a topic plan writes the draft (§6.6), so a plan cancelled there
 * would otherwise show here and go live with the next change of the other parameters (#1076).
 */
const storedPathRules = computed<PathRuleJson[]>(() => {
  const rules = published.value?.definition.params.pathRules
  return Array.isArray(rules) ? (rules as PathRuleJson[]) : []
})
const summary = computed(() =>
  fields.value.filter((f) => f.kind !== 'json').map((f) => {
    const value = current.value?.definition.params[f.name] ?? f.schema.default
    return { label: f.label, value: f.kind === 'select' ? (PIPELINE.options[String(value)] ?? String(value)) : String(value) }
  }),
)

function display(kind: string, value: unknown): string {
  return kind === 'json' ? JSON.stringify(value ?? null, null, 2) : value === undefined || value === null ? '' : String(value)
}

async function load(): Promise<void> {
  pipeline.value = await api.pipelines.get(props.source.pipelineId).catch(() => null)
  const definition = current.value?.definition
  if (definition) template.value = await api.templates.get(definition.template, definition.templateVersion).catch(() => null)
}

function edit(): void {
  for (const f of editable.value) {
    values[f.name] = display(f.kind, current.value?.definition.params[f.name] ?? f.schema.default)
    fieldErrors[f.name] = undefined
  }
  error.value = null
  open.value = true
}

function parameters(): Record<string, unknown> | null {
  const result: Record<string, unknown> = hasPathRules.value ? { pathRules: storedPathRules.value } : {}
  let ok = true
  for (const f of editable.value) {
    const text = (values[f.name] ?? '').trim()
    fieldErrors[f.name] = undefined
    if (f.kind === 'integer') {
      if (!/^\d+$/.test(text)) {
        fieldErrors[f.name] = PIPELINE.invalidNumber
        ok = false
      } else result[f.name] = Number(text)
    } else if (f.kind === 'json') {
      try {
        result[f.name] = JSON.parse(text || 'null')
      } catch {
        fieldErrors[f.name] = PIPELINE.invalidJson
        ok = false
      }
    } else result[f.name] = text
  }
  return ok ? result : null
}

async function publish(): Promise<void> {
  const params = parameters()
  if (!params || !pipeline.value) return
  planning.value = true
  error.value = null
  try {
    const started = await api.pipelines.plan(pipeline.value.id, {
      parameters: params, version: pipeline.value.version, ...(copyOnWrite.value ? { collectionId: props.source.collectionId } : {}),
    })
    open.value = false
    void router.push({ name: 'publication', params: { id: started.publicationId } })
  } catch (e) {
    error.value = e
  } finally {
    planning.value = false
  }
}

onMounted(load)
</script>

<template>
  <UiCard v-if="pipeline" :title="PIPELINE.title" data-test="pipeline">
    <dl class="m-0">
      <DetailRow :label="PIPELINE.name">{{ pipeline.name }}</DetailRow>
      <DetailRow v-if="published" :label="PIPELINE.version">{{ published.number }}<span v-if="draft" class="ml-2 text-fg-2">· {{ PIPELINE.draft(draft.number) }}</span></DetailRow>
      <DetailRow v-for="s in summary" :key="s.label" :label="s.label">{{ s.value }}</DetailRow>
      <DetailRow v-if="hasPathRules" :label="PIPELINE.topics" data-test="pipeline-topics">
        <span class="grid gap-1">
          <span v-if="storedPathRules.length === 0" class="text-fg-3">{{ PIPELINE.pathRulesNone }}</span>
          <span v-for="(r, i) in storedPathRules" :key="i" data-test="pipeline-topic"><span v-if="tagsOf(r).length" class="font-mono">{{ tagsOf(r).join(', ') }}: </span>{{ ruleInWords(r) }}</span>
          <RouterLink
            :to="{ name: 'collection', params: { id: source.collectionId }, query: { tab: 'topics' } }" class="text-accent-ink underline" data-test="pipeline-topics-link"
          >{{ PIPELINE.pathRulesEdit }}</RouterLink>
        </span>
      </DetailRow>
    </dl>
    <div v-if="canPublish && template" class="mt-4"><UiButton icon="tune" data-test="edit-pipeline" @click="edit">{{ PIPELINE.edit }}</UiButton></div>

    <UiDrawer v-model:open="open" :title="PIPELINE.edit" :subtitle="pipeline.name">
      <form id="pipeline-form" class="grid gap-5" novalidate @submit.prevent="publish">
        <p class="text-fg-2">{{ PIPELINE.editLead }}</p>
        <p v-if="copyOnWrite" class="rounded-card bg-sunken p-3 text-small" data-test="copy-on-write">{{ PIPELINE.platformShared }}</p>
        <p v-if="hasPathRules" class="m-0 text-small text-fg-2" data-test="pipeline-topics-kept">{{ PIPELINE.pathRulesKept }}</p>
        <template v-for="f in editable" :key="f.name">
          <SelectField v-if="f.kind === 'select'" :id="`param-${f.name}`" v-model="values[f.name]!" :label="f.label" :options="f.options" />
          <TextField
            v-else-if="f.kind === 'integer'" :id="`param-${f.name}`" v-model="values[f.name]!" :label="f.label" :hint="f.hint" inputmode="numeric" short
            :error="fieldErrors[f.name]"
          />
          <TextAreaField v-else :id="`param-${f.name}`" v-model="values[f.name]!" :label="f.label" :hint="f.hint" :error="fieldErrors[f.name]" mono />
        </template>
        <InlineError v-if="error" :message="problemMessage(error)" />
      </form>
      <template #footer>
        <UiButton variant="primary" type="submit" form="pipeline-form" :loading="planning" data-test="plan">{{ PIPELINE.publish }}</UiButton>
        <UiButton variant="quiet" @click="open = false">{{ COMMON.cancel }}</UiButton>
      </template>
    </UiDrawer>
  </UiCard>
</template>
