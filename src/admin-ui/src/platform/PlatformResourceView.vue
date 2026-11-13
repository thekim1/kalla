<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { api } from '@/api'
import { request } from '@/api/client'
import type { ApiKey, GatewayModel } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { COMMON, PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import CopyValue from './CopyValue.vue'
import CrawlSettingsForm from './CrawlSettingsForm.vue'
import EmbeddingCanaryPanel from './EmbeddingCanaryPanel.vue'
import EmbeddingProfilesPanel from './EmbeddingProfilesPanel.vue'
import GatewayContextPanel from './GatewayContextPanel.vue'
import PromptTextsPanel from './PromptTextsPanel.vue'
import SecretOnce from './SecretOnce.vue'
import GenericForm from './GenericForm.vue'
import KeyForm from './KeyForm.vue'
import { withModelFields } from './modelFields'
import { gatewayAdminUrl, gatewayLink, resourceById, rowsOf, urlOf, type Operation, type ResourceColumn, type Row, type RowAction } from './resources'
import { fieldsOf, loadOpenApi, requestSchema, type FieldSpec, type OpenApiDocument } from './schema'

// A platform object's list and its generic form (admin design §5.8): create, edit, and the few non-CRUD actions.
const props = defineProps<{ resource: string }>()
const ui = useUiStore()
const resource = computed(() => resourceById(props.resource))
const rows = useLoadable<Row[]>([])
const adminUrl = ref<string | null>(null)

interface OpenForm {
  title: string
  submitLabel: string
  operation: Operation
  url: string
  fields: FieldSpec[]
  initial: Row
  /** The gateway mirror with the platform key's availability, for the model fields' pickers (#466). */
  models: GatewayModel[]
}
const drawerOpen = ref(false)
const selected = ref<Row | null>(null)
// Shallow, so `openForm.operation` stays the registry's object and compares equal to `resource.update`.
const openForm = shallowRef<OpenForm | null>(null)
const formKey = ref(0)
const opening = ref(false)
const openError = ref<unknown>(null)
const secret = ref<string | null>(null)
const confirming = ref<RowAction | null>(null)

const columns = computed<ResourceColumn[]>(() => resource.value?.columns ?? [])
const details = computed(() => (selected.value && resource.value?.details ? resource.value.details(selected.value) : []))
// A row without an edit opens its details alone; with one, they stand above its form.
const showDetails = computed(() => details.value.length > 0 && (!editable(selected.value!) || openForm.value?.operation === resource.value!.update))
const drawerTitle = computed(() => openForm.value?.title ?? (selected.value && resource.value ? rowTitle(selected.value) : resource.value?.label ?? ''))
const gatewayHref = computed(() => (adminUrl.value && resource.value?.gatewayPage ? gatewayLink(adminUrl.value, resource.value.gatewayPage) : null))
// A row's edit or a create can have a form of its own; an action's form is always the generic one.
const ownForm = computed(() => {
  const r = resource.value
  const form = openForm.value
  if (!r?.form || !form) return undefined
  if (form.operation === r.update && selected.value) return r.form(selected.value)
  return form.operation === r.create ? r.form(null) : undefined
})
// Every generic drawer shows the row's id with a copy button (admin design §5.8).
const rowId = computed(() => (typeof selected.value?.id === 'string' ? selected.value.id : null))
// The Keys page's rows are keys (`KeyDto`).
const selectedKey = computed(() => selected.value as unknown as ApiKey | null)

async function load(): Promise<void> {
  if (!resource.value) return
  if (resource.value.gatewayPage) void gatewayAdminUrl().then((url) => (adminUrl.value = url))
  await rows.run(() => rowsOf(resource.value!))
}

function editable(row: Row): boolean {
  const r = resource.value!
  return !!r.update && r.editable?.(row) !== false
}

function rowTitle(row: Row): string {
  const r = resource.value!
  return String(row[r.columns[0]!.key] ?? row[r.key])
}

function cell(value: unknown): string {
  if (value === null || value === undefined || value === '') return '–'
  if (typeof value === 'boolean') return value ? COMMON.yes : COMMON.no
  if (Array.isArray(value)) return value.join(', ')
  if (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value)) return formatDateTime(value)
  return String(value)
}

// Settings sections share one entry with a placeholder; the document has one path per section.
function schemaOf(doc: OpenApiDocument, operation: Operation, url: string) {
  return requestSchema(doc, operation.method, operation.path) ?? requestSchema(doc, operation.method, url)
}

async function show(title: string, submitLabel: string, operation: Operation, row: Row | null, initial: () => Promise<Row> | Row): Promise<void> {
  const r = resource.value!
  drawerOpen.value = true
  secret.value = null
  openError.value = null
  opening.value = true
  try {
    const doc = await loadOpenApi()
    const url = urlOf(operation.path, row, r.key)
    const modelFields = (row && operation === r.update ? r.modelFields?.(row) : undefined) ?? []
    // Without `level`: availability at the platform ceiling, where V32 checks the settings' models.
    const [start, models] = await Promise.all([initial(), modelFields.length ? api.gateway.models() : []])
    openForm.value = {
      title,
      submitLabel,
      operation,
      url,
      fields: withModelFields(fieldsOf(doc, schemaOf(doc, operation, url)), modelFields),
      initial: start,
      models,
    }
    formKey.value++
  } catch (e) {
    openForm.value = null
    openError.value = e
  } finally {
    opening.value = false
  }
}

function create(): void {
  const r = resource.value!
  selected.value = null
  void show(PLATFORM.form.createTitle(r.label), PLATFORM.form.create, r.create!, null, () => ({}))
}

function edit(row: Row): void {
  const r = resource.value!
  selected.value = row
  if (!editable(row)) {
    openForm.value = null
    openError.value = null
    secret.value = null
    drawerOpen.value = !!r.details
    return
  }
  void show(rowTitle(row), PLATFORM.form.save, r.update!, row, () => (r.load ? r.load(row) : row))
}

async function runAction(action: RowAction): Promise<void> {
  const r = resource.value!
  const row = selected.value!
  const doc = await loadOpenApi()
  if (fieldsOf(doc, schemaOf(doc, action, urlOf(action.path, row, r.key))).length > 0) {
    await show(action.label, action.label, action, row, () => ({}))
  } else if (action.confirm && confirming.value !== action) {
    confirming.value = action
  } else {
    confirming.value = null
    try {
      saved(await request(action.method.toUpperCase() as 'POST' | 'PUT', urlOf(action.path, row, r.key)))
    } catch (e) {
      openError.value = e
    }
  }
}

function saved(response: unknown): void {
  const value = response && typeof response === 'object' ? (response as Row) : null
  // A new, rotated or revealed key carries its secret once (identity design §6.4).
  secret.value = typeof value?.secret === 'string' ? value.secret : null
  if (!secret.value) drawerOpen.value = false
  openForm.value = null
  ui.notify(PLATFORM.form.saved)
  void load()
}

watch(() => props.resource, () => {
  drawerOpen.value = false
  adminUrl.value = null
  void load()
}, { immediate: true })
</script>

<template>
  <template v-if="resource">
    <PageHeader :title="resource.label" :description="resource.description">
      <UiButton
        v-if="gatewayHref && resource.gatewayPage" :href="gatewayHref" target="_blank" rel="noopener noreferrer" icon="open_in_new" data-test="gateway-page"
      >
        {{ resource.gatewayPage.label }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span>
      </UiButton>
      <UiButton v-if="resource.create" variant="primary" icon="add" data-test="create" @click="create">{{ PLATFORM.form.create }}</UiButton>
    </PageHeader>
    <template v-for="panel in resource.panels ?? []" :key="panel">
      <GatewayContextPanel v-if="panel === 'gateway-context'" class="mb-6" />
      <EmbeddingProfilesPanel v-else-if="panel === 'embedding-profiles'" class="mb-6" />
      <EmbeddingCanaryPanel v-else-if="panel === 'embedding-canary'" class="mb-6" />
      <PromptTextsPanel v-else-if="panel === 'prompt-texts'" class="mb-6" />
    </template>
    <AsyncState :loading="rows.loading.value" :error="rows.error.value" :empty="rows.data.value.length === 0" @retry="load">
      <DataTable
        :columns="columns" :rows="rows.data.value" :row-key="(row) => String(row[resource!.key])" :caption="resource.label"
        :clickable="!!resource.update || !!resource.details" @row-click="edit"
      >
        <template v-for="c in columns" :key="c.key" #[`cell-${c.key}`]="{ row }">{{ cell(c.value ? c.value(row) : row[c.key]) }}</template>
      </DataTable>
    </AsyncState>

    <UiDrawer v-model:open="drawerOpen" :title="drawerTitle" wide>
      <div v-if="secret" class="grid gap-3">
        <SecretOnce :secret="secret" />
        <div><UiButton variant="quiet" @click="drawerOpen = false">{{ COMMON.close }}</UiButton></div>
      </div>
      <template v-else>
        <div v-if="rowId" class="mb-6 grid gap-1" data-test="row-id">
          <span class="text-caption text-fg-3">{{ PLATFORM.form.id }}</span>
          <CopyValue :value="rowId" />
        </div>
        <p v-if="opening">{{ COMMON.loading }}</p>
        <InlineError v-if="openError" :message="problemMessage(openError)" class="mb-4" />
        <template v-if="showDetails">
          <section v-for="section in details" :key="section.title" class="mb-6" data-test="details">
            <h3 class="mb-2 font-medium">{{ section.title }}</h3>
            <ul v-if="section.lines" class="grid gap-1">
              <li v-for="line in section.lines" :key="line">{{ line }}</li>
            </ul>
            <dl v-if="section.items" class="grid grid-cols-[1fr_auto] gap-x-6 gap-y-1">
              <template v-for="item in section.items" :key="item.label">
                <dt class="text-fg-2">{{ item.label }}</dt>
                <dd class="tabular">{{ item.value }}</dd>
              </template>
            </dl>
            <p v-if="section.note" class="mt-2 text-small text-fg-3">{{ section.note }}</p>
          </section>
        </template>
        <div v-if="selected && resource.actions?.length && openForm?.operation === resource.update" class="mb-6 flex flex-wrap gap-2">
          <UiButton v-for="action in resource.actions" :key="action.id" :data-test="`action-${action.id}`" @click="runAction(action)">{{ action.label }}</UiButton>
        </div>
        <CrawlSettingsForm
          v-if="openForm && ownForm === 'crawl-settings'" :key="formKey" :initial="openForm.initial" :url="openForm.url" :submit-label="openForm.submitLabel"
          @saved="saved" @cancel="drawerOpen = false"
        />
        <KeyForm
          v-else-if="openForm && ownForm === 'key'" :key="formKey" :form-id="`platform-${resource.id}`" :api-key="selectedKey" :client-id="null"
          @saved="saved" @cancel="drawerOpen = false"
        />
        <GenericForm
          v-else-if="openForm" :key="formKey" :form-id="`platform-${resource.id}`" :fields="openForm.fields" :initial="openForm.initial"
          :method="openForm.operation.method" :url="openForm.url" :submit-label="openForm.submitLabel" :models="openForm.models" @saved="saved" @cancel="drawerOpen = false"
        />
      </template>
    </UiDrawer>
    <ConfirmDialog
      v-if="confirming" :open="!!confirming" :title="confirming.label" :consequence="confirming.confirm" :confirm-label="confirming.label" danger
      @confirm="runAction(confirming)" @update:open="confirming = null"
    />
  </template>
</template>
