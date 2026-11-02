<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Pipeline, Source, TemplateDetail } from '@/api/types'
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
const canPublish = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-pipeline-editor']))
const platformAdmin = computed(() => auth.hasAnyRole(['rag-platform-admin']))

const published = computed(() => pipeline.value?.versions.find((v) => v.id === pipeline.value?.publishedVersionId) ?? null)
const draft = computed(() => pipeline.value?.versions.find((v) => v.state === 'draft') ?? null)
/** The parameters the form starts from: the draft's when there is one, else the published version's. */
const current = computed(() => draft.value ?? published.value)
const copyOnWrite = computed(() => pipeline.value?.departmentId === null && !platformAdmin.value)
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
  for (const f of fields.value) {
    values[f.name] = display(f.kind, current.value?.definition.params[f.name] ?? f.schema.default)
    fieldErrors[f.name] = undefined
  }
  error.value = null
  open.value = true
}

function parameters(): Record<string, unknown> | null {
  const result: Record<string, unknown> = {}
  let ok = true
  for (const f of fields.value) {
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
    <DetailRow :label="PIPELINE.name">{{ pipeline.name }}</DetailRow>
    <DetailRow v-if="published" :label="PIPELINE.version">{{ published.number }}<span v-if="draft" class="ml-2 text-fg-2">· {{ PIPELINE.draft(draft.number) }}</span></DetailRow>
    <DetailRow v-for="s in summary" :key="s.label" :label="s.label">{{ s.value }}</DetailRow>
    <div v-if="canPublish && template" class="mt-4"><UiButton icon="tune" data-test="edit-pipeline" @click="edit">{{ PIPELINE.edit }}</UiButton></div>

    <UiDrawer v-model:open="open" :title="PIPELINE.edit" :subtitle="pipeline.name">
      <form id="pipeline-form" class="grid gap-5" novalidate @submit.prevent="publish">
        <p class="text-fg-2">{{ PIPELINE.editLead }}</p>
        <p v-if="copyOnWrite" class="rounded-card bg-sunken p-3 text-small" data-test="copy-on-write">{{ PIPELINE.platformShared }}</p>
        <template v-for="f in fields" :key="f.name">
          <SelectField v-if="f.kind === 'select'" :id="`param-${f.name}`" v-model="values[f.name]!" :label="f.label" :options="f.options" />
          <TextField
            v-else-if="f.kind === 'integer'" :id="`param-${f.name}`" v-model="values[f.name]!" :label="f.label" :hint="f.hint" inputmode="numeric"
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
