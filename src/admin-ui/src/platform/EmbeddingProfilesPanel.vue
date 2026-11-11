<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import { http } from '@/api/client'
import type { GatewayModel } from '@/api/types'
import DataTable from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useUiStore } from '@/stores/ui'
import { COMMON, EMBEDDING_PROFILES as E, PLATFORM } from '@/utils/labels'
import { embeddingTestMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import GenericForm from './GenericForm.vue'
import { withModelFields } from './modelFields'
import { fieldsOf, loadOpenApi, requestSchema, type FieldSpec } from './schema'

// Embedding profiles on Platform → Gateway models (admin design §4.5, §5.8; Decisions "Editable embedding profiles before
// use"): the generic form of PUT /api/embedding-profiles/{id}, with Test on the values as entered. Every field may change
// until content is indexed with the profile; then the server allows only the ceiling, and the form says so.

export interface EmbeddingProfile {
  id: string
  name: string
  gatewayModel: string
  dimensions: number
  requestDimensions: boolean
  maxInputTokens: number
  maxResidency: string
  isDefault: boolean
  state: string
  inUse: boolean
  version: string
}

interface TestResult {
  ok: boolean
  dimensions: number | null
  residency: string | null
  error: string | null
  detail: string | null
}

const UPDATE_PATH = '/api/embedding-profiles/{id}'
/** The ceilings a profile may have: at most the platform ceiling Eu (until M2). */
const CEILINGS = ['OnPrem', 'Eu']

const ui = useUiStore()
const rows = ref<EmbeddingProfile[]>([])
const loadError = ref<unknown>(null)
const loaded = ref(false)
const selected = ref<EmbeddingProfile | null>(null)
const drawerOpen = ref(false)
const fields = ref<FieldSpec[]>([])
const models = ref<GatewayModel[]>([])
const openError = ref<unknown>(null)
const formKey = ref(0)
const testing = ref(false)
const testResult = ref<{ ok: boolean; text: string } | null>(null)
const testError = ref<unknown>(null)

const columns = [
  { key: 'name', label: E.name },
  { key: 'gatewayModel', label: E.model },
  { key: 'dimensions', label: E.dimensions, align: 'right' as const },
  { key: 'maxResidency', label: E.ceiling },
  { key: 'use', label: E.use },
  { key: 'actions', label: '' },
]

async function load(): Promise<void> {
  loadError.value = null
  try {
    rows.value = await http.get<EmbeddingProfile[]>('/api/embedding-profiles')
  } catch (e) {
    loadError.value = e
  } finally {
    loaded.value = true
  }
}

/** The ceiling as a choice of the allowed levels; the model as a picker over the mirror's embedding models. */
function profileFields(all: FieldSpec[]): FieldSpec[] {
  return withModelFields(all, [{ name: 'gatewayModel', modelKind: 'embedding' }]).map((f) =>
    f.name === 'maxResidency' ? { ...f, kind: 'select' as const, options: CEILINGS, required: true } : f,
  )
}

async function edit(row: EmbeddingProfile): Promise<void> {
  selected.value = row
  drawerOpen.value = true
  openError.value = null
  testResult.value = null
  testError.value = null
  fields.value = []
  try {
    const [doc, mirror] = await Promise.all([loadOpenApi(), api.gateway.models()])
    fields.value = profileFields(fieldsOf(doc, requestSchema(doc, 'put', UPDATE_PATH)))
    models.value = mirror
    formKey.value++
  } catch (e) {
    openError.value = e
  }
}

async function test(collect: () => Record<string, unknown> | null): Promise<void> {
  const body = collect()
  if (!body || !selected.value) return
  testing.value = true
  testResult.value = null
  testError.value = null
  try {
    const { gatewayModel, dimensions, requestDimensions, maxResidency } = body
    const result = await http.post<TestResult>(`/api/embedding-profiles/${encodeURIComponent(selected.value.id)}/test`, {
      gatewayModel,
      dimensions,
      requestDimensions,
      maxResidency,
    })
    const tested = {
      gatewayModel: String(gatewayModel ?? selected.value.gatewayModel),
      dimensions: Number(dimensions ?? selected.value.dimensions),
      maxResidency: String(maxResidency ?? selected.value.maxResidency),
    }
    testResult.value = { ok: result.ok, text: embeddingTestMessage(result, tested) }
  } catch (e) {
    testError.value = e
  } finally {
    testing.value = false
  }
}

function saved(): void {
  drawerOpen.value = false
  ui.notify(E.saved)
  void load()
}

onMounted(load)
</script>

<template>
  <UiCard :title="E.title" heading-id="embedding-profiles-title" data-test="embedding-profiles">
    <p class="mb-4 text-fg-2">{{ E.lead }}</p>
    <InlineError v-if="loadError" :message="problemMessage(loadError)" class="mb-4" />
    <p v-if="!loaded">{{ COMMON.loading }}</p>
    <p v-else-if="rows.length === 0 && !loadError" data-test="profiles-empty">{{ E.none }}</p>
    <DataTable v-else-if="rows.length > 0" :columns="columns" :rows="rows" :row-key="(row) => row.id" :caption="E.title">
      <template #cell-name="{ row }">
        {{ row.name }}
        <UiBadge v-if="row.isDefault" tone="accent" :label="E.isDefault" class="ml-2" />
      </template>
      <template #cell-gatewayModel="{ row }">{{ row.gatewayModel }}</template>
      <template #cell-dimensions="{ row }">{{ row.dimensions }}</template>
      <template #cell-maxResidency="{ row }">{{ row.maxResidency }}</template>
      <template #cell-use="{ row }"><UiBadge :tone="row.inUse ? 'neutral' : 'ok'" :label="row.inUse ? E.inUse : E.unused" /></template>
      <template #cell-actions="{ row }">
        <UiButton variant="quiet" icon="edit" :data-test="`profile-edit-${row.name}`" @click="edit(row)">{{ E.edit }}</UiButton>
      </template>
    </DataTable>

    <UiDrawer v-model:open="drawerOpen" :title="selected ? E.editTitle(selected.name) : E.title" wide>
      <InlineError v-if="openError" :message="problemMessage(openError)" class="mb-4" />
      <p v-if="selected?.inUse" class="mb-4 rounded-card bg-sunken p-3" data-test="profile-in-use">{{ E.inUseNote }}</p>
      <p v-if="!openError && fields.length === 0">{{ COMMON.loading }}</p>
      <GenericForm
        v-else-if="selected && fields.length > 0" :key="formKey" form-id="embedding-profile" :fields="fields" :initial="{ ...selected }"
        method="put" :url="`/api/embedding-profiles/${encodeURIComponent(selected.id)}`" :submit-label="PLATFORM.form.save" :models="models"
        @saved="saved" @cancel="drawerOpen = false"
      >
        <template #extra="{ collect }">
          <div class="grid gap-2 rounded-card border border-border p-3" data-test="profile-test">
            <p class="text-small text-fg-2">{{ E.testLead }}</p>
            <div>
              <UiButton icon="science" :loading="testing" data-test="profile-test-run" @click="test(collect)">{{ testing ? E.testing : E.test }}</UiButton>
            </div>
            <p v-if="testResult" role="status" :class="testResult.ok ? 'text-ok' : 'text-danger'" data-test="profile-test-result">{{ testResult.text }}</p>
            <InlineError v-if="testError" :message="problemMessage(testError)" />
          </div>
        </template>
      </GenericForm>
    </UiDrawer>
  </UiCard>
</template>
