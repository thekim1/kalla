<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { request } from '@/api/client'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useCopy } from '@/composables/useCopy'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { COMMON, PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import GenericForm from './GenericForm.vue'
import { resourceById, rowsOf, urlOf, type Operation, type Row, type RowAction } from './resources'
import { fieldsOf, loadOpenApi, requestSchema, type FieldSpec, type OpenApiDocument } from './schema'

// A platform object's list and its generic form (admin design §5.8): create, edit, and the few non-CRUD actions.
const props = defineProps<{ resource: string }>()
const ui = useUiStore()
const { copy } = useCopy()
const resource = computed(() => resourceById(props.resource))
const rows = useLoadable<Row[]>([])

interface OpenForm {
  title: string
  submitLabel: string
  operation: Operation
  url: string
  fields: FieldSpec[]
  initial: Row
}
const drawerOpen = ref(false)
const selected = ref<Row | null>(null)
const openForm = ref<OpenForm | null>(null)
const formKey = ref(0)
const opening = ref(false)
const openError = ref<unknown>(null)
const secret = ref<string | null>(null)
const confirming = ref<RowAction | null>(null)

const columns = computed<Column[]>(() => resource.value?.columns ?? [])

async function load(): Promise<void> {
  if (resource.value) await rows.run(() => rowsOf(resource.value!))
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
    openForm.value = {
      title,
      submitLabel,
      operation,
      url,
      fields: fieldsOf(doc, schemaOf(doc, operation, url)),
      initial: await initial(),
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
  if (!r.update) {
    drawerOpen.value = false
    return
  }
  void show(String(row[r.columns[0]!.key] ?? row[r.key]), PLATFORM.form.save, r.update, row, () => (r.load ? r.load(row) : row))
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
  void load()
}, { immediate: true })
</script>

<template>
  <template v-if="resource">
    <PageHeader :title="resource.label" :description="resource.description">
      <UiButton v-if="resource.create" variant="primary" icon="add" data-test="create" @click="create">{{ PLATFORM.form.create }}</UiButton>
    </PageHeader>
    <AsyncState :loading="rows.loading.value" :error="rows.error.value" :empty="rows.data.value.length === 0" @retry="load">
      <DataTable
        :columns="columns" :rows="rows.data.value" :row-key="(row) => String(row[resource!.key])" :caption="resource.label"
        :clickable="!!resource.update" @row-click="edit"
      >
        <template v-for="c in columns" :key="c.key" #[`cell-${c.key}`]="{ row }">{{ cell(row[c.key]) }}</template>
      </DataTable>
    </AsyncState>

    <UiDrawer v-model:open="drawerOpen" :title="openForm?.title ?? resource.label" wide>
      <div v-if="secret" class="grid gap-3" data-test="secret">
        <p class="font-medium">{{ PLATFORM.form.secretOnce }}</p>
        <code class="break-all rounded-card bg-sunken p-3 font-mono">{{ secret }}</code>
        <div class="flex gap-2">
          <UiButton icon="content_copy" @click="copy(secret)">{{ PLATFORM.form.copy }}</UiButton>
          <UiButton variant="quiet" @click="drawerOpen = false">{{ COMMON.close }}</UiButton>
        </div>
      </div>
      <template v-else>
        <p v-if="opening">{{ COMMON.loading }}</p>
        <InlineError v-if="openError" :message="problemMessage(openError)" class="mb-4" />
        <div v-if="selected && resource.actions?.length && openForm?.operation === resource.update" class="mb-6 flex flex-wrap gap-2">
          <UiButton v-for="action in resource.actions" :key="action.id" :data-test="`action-${action.id}`" @click="runAction(action)">{{ action.label }}</UiButton>
        </div>
        <GenericForm
          v-if="openForm" :key="formKey" :form-id="`platform-${resource.id}`" :fields="openForm.fields" :initial="openForm.initial"
          :method="openForm.operation.method" :url="openForm.url" :submit-label="openForm.submitLabel" @saved="saved" @cancel="drawerOpen = false"
        />
      </template>
    </UiDrawer>
    <ConfirmDialog
      v-if="confirming" :open="!!confirming" :title="confirming.label" :consequence="confirming.confirm" :confirm-label="confirming.label" danger
      @confirm="runAction(confirming)" @update:open="confirming = null"
    />
  </template>
</template>
