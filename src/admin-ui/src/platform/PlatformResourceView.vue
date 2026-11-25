<script setup lang="ts">
import { computed, inject, ref, shallowRef, watch } from 'vue'
import { routeLocationKey, routerKey } from 'vue-router'
import { api } from '@/api'
import { request } from '@/api/client'
import type { GatewayModel } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import DataTable from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { gatewayHref } from '@/utils/gateway'
import { COMMON, PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import CopyValue from './CopyValue.vue'
import EmbeddingCanaryPanel from './EmbeddingCanaryPanel.vue'
import EmbeddingProfilesPanel from './EmbeddingProfilesPanel.vue'
import GatewayContextPanel from './GatewayContextPanel.vue'
import SecretOnce from './SecretOnce.vue'
import GenericForm from './GenericForm.vue'
import { withModelFields } from './modelFields'
import { gatewayAdminUrl, resourceById, rowsOf, urlOf, type Operation, type ResourceColumn, type ResourcePanel, type Row, type RowAction } from './resources'
import { fieldsOf, loadOpenApi, requestSchema, type FieldSpec, type OpenApiDocument } from './schema'

// A platform object's list and its generic form (admin design §5.8): create, edit, and the few non-CRUD actions. `embedded`:
// a section of the Settings page (admin design §5.10), which has the page title; the description and buttons stay.
const props = defineProps<{ resource: string; embedded?: boolean }>()
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

const columns = computed<ResourceColumn[]>(() => resource.value?.columns ?? [])

// The page's tabs (resource.tabs): the list's tab is `list`. Injected without a warning where there is no router (the
// Settings page's embedded lists in unit tests); the address names a tab other than the first.
const router = inject(routerKey, null)
const route = inject(routeLocationKey, null)
const tabOptions = computed(() => {
  const t = resource.value?.tabs
  return t ? [{ value: 'list', label: t.list }, ...t.others.map((o) => ({ value: o.id, label: o.label }))] : []
})
const tab = ref(String(route?.query.tab ?? 'list'))
const tabPanels = computed<readonly ResourcePanel[]>(() => resource.value?.tabs?.others.find((o) => o.id === tab.value)?.panels ?? [])
watch(tabOptions, (options) => {
  if (options.length && !options.some((o) => o.value === tab.value)) tab.value = 'list'
}, { immediate: true })
function switchTab(value: string): void {
  tab.value = value
  if (router && route) void router.replace({ query: { ...route.query, tab: value === 'list' ? undefined : value } })
}

// The FakeGateway's models (resource.testRow) behind Show test models, offered only while there are any.
const showTest = ref(false)
const testCount = computed(() => (resource.value?.testRow ? rows.data.value.filter(resource.value.testRow).length : 0))
const shownRows = computed(() => (showTest.value || !resource.value?.testRow ? rows.data.value : rows.data.value.filter((r) => !resource.value!.testRow!(r))))
const details = computed(() => (selected.value && resource.value?.details ? resource.value.details(selected.value) : []))
// A row without an edit opens its details alone; with one, they stand above its form.
const showDetails = computed(() => details.value.length > 0 && (!editable(selected.value!) || openForm.value?.operation === resource.value!.update))
const drawerTitle = computed(() => openForm.value?.title ?? (selected.value && resource.value ? rowTitle(selected.value) : resource.value?.label ?? ''))
const gatewayPageHref = computed(() => (resource.value?.gatewayPage ? gatewayHref(adminUrl.value, resource.value.gatewayPage.path) : null))
// Every generic drawer shows the row's id with a copy button (admin design §5.8).
const rowId = computed(() => (typeof selected.value?.id === 'string' ? selected.value.id : null))

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

// A path with a placeholder, or (for a path the document lists per row) the row's own.
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
  } else {
    try {
      saved(await request(action.method.toUpperCase() as 'POST' | 'PUT', urlOf(action.path, row, r.key)))
    } catch (e) {
      openError.value = e
    }
  }
}

// The row's delete (admin design §5.8): asked first; the row's version guards it like an edit.
const removing = ref(false)
async function remove(): Promise<void> {
  const r = resource.value!
  const row = selected.value!
  if (!r.remove || !window.confirm([PLATFORM.actions.deleteConfirm(rowTitle(row)), r.remove.deleteNote].filter(Boolean).join(' '))) return
  removing.value = true
  openError.value = null
  try {
    await request('DELETE', urlOf(r.remove.path, row, r.key), { query: { version: String(row.version ?? '') } })
    drawerOpen.value = false
    openForm.value = null
    ui.notify(PLATFORM.form.deleted)
    void load()
  } catch (e) {
    openError.value = e
  } finally {
    removing.value = false
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

// A shown-once secret does not stay in memory after the drawer closes.
watch(drawerOpen, (open) => {
  if (!open) secret.value = null
})

watch(() => props.resource, () => {
  drawerOpen.value = false
  adminUrl.value = null
  void load()
}, { immediate: true })
</script>

<template>
  <template v-if="resource">
    <div v-if="embedded && resource.create" class="mb-4 flex justify-end">
      <UiButton variant="primary" icon="add" data-test="create" @click="create">{{ PLATFORM.form.create }}</UiButton>
    </div>
    <PageHeader v-else-if="!embedded" :title="resource.label" :description="resource.description">
      <UiButton
        v-if="gatewayPageHref && resource.gatewayPage" :href="gatewayPageHref" target="_blank" rel="noopener noreferrer" icon="open_in_new" data-test="gateway-page"
      >
        {{ resource.gatewayPage.label }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span>
      </UiButton>
      <UiButton v-if="resource.create" variant="primary" icon="add" data-test="create" @click="create">{{ PLATFORM.form.create }}</UiButton>
    </PageHeader>
    <template v-for="panel in resource.panels ?? []" :key="panel">
      <GatewayContextPanel v-if="panel === 'gateway-context'" class="mb-6" />
      <EmbeddingProfilesPanel v-else-if="panel === 'embedding-profiles'" class="mb-6" />
      <EmbeddingCanaryPanel v-else-if="panel === 'embedding-canary'" class="mb-6" />
    </template>
    <UiTabs v-if="tabOptions.length" :id="`resource-${resource.id}`" :model-value="tab" :tabs="tabOptions" :label="resource.tabs!.label" class="mb-6" @update:model-value="switchTab" />
    <div
      v-if="tabOptions.length && tab !== 'list'" :id="`resource-${resource.id}-panel-${tab}`" role="tabpanel"
      :aria-labelledby="`resource-${resource.id}-tab-${tab}`" data-test="tab-panels"
    >
      <template v-for="panel in tabPanels" :key="panel">
        <EmbeddingProfilesPanel v-if="panel === 'embedding-profiles'" class="mb-6" />
        <EmbeddingCanaryPanel v-else-if="panel === 'embedding-canary'" class="mb-6" />
        <GatewayContextPanel v-else-if="panel === 'gateway-context'" class="mb-6" />
      </template>
    </div>
    <div
      v-else :id="tabOptions.length ? `resource-${resource.id}-panel-list` : undefined" :role="tabOptions.length ? 'tabpanel' : undefined"
      :aria-labelledby="tabOptions.length ? `resource-${resource.id}-tab-list` : undefined"
    >
      <h2 v-if="resource.listTitle" class="mb-3 text-title-3" data-test="list-title">{{ resource.listTitle }}</h2>
      <div v-if="testCount > 0" class="mb-4" data-test="show-test">
        <CheckField v-model="showTest" :label="PLATFORM.gatewayModels.showTest" :description="PLATFORM.gatewayModels.showTestHint(testCount)" />
      </div>
      <AsyncState :loading="rows.loading.value" :error="rows.error.value" :empty="shownRows.length === 0" @retry="load">
        <DataTable
          :columns="columns" :rows="shownRows" :row-key="(row) => String(row[resource!.key])" :caption="resource.label"
          :clickable="!!resource.update || !!resource.details" @row-click="edit"
        >
          <template v-for="c in columns" :key="c.key" #[`cell-${c.key}`]="{ row }">{{ cell(c.value ? c.value(row) : row[c.key]) }}</template>
        </DataTable>
      </AsyncState>
    </div>

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
        <div v-if="selected && resource.remove && openForm?.operation === resource.update" class="mb-6 flex justify-end">
          <UiButton variant="danger" icon="delete" :loading="removing" data-test="delete" @click="remove">{{ PLATFORM.actions.delete }}</UiButton>
        </div>
        <GenericForm
          v-if="openForm" :key="formKey" :form-id="`platform-${resource.id}`" :fields="openForm.fields" :initial="openForm.initial"
          :method="openForm.operation.method" :url="openForm.url" :submit-label="openForm.submitLabel" :models="openForm.models" @saved="saved" @cancel="drawerOpen = false"
        />
      </template>
    </UiDrawer>
  </template>
</template>
