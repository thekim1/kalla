<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'
import { http } from '@/api/client'
import type { GatewayModel } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import { useUiStore } from '@/stores/ui'
import { COMMON, PLATFORM, SETTINGS_PAGE } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import CrawlSettingsForm from './CrawlSettingsForm.vue'
import GenericForm from './GenericForm.vue'
import PiiSettingsForm from './PiiSettingsForm.vue'
import { withModelFields } from './modelFields'
import { fieldsOf, loadOpenApi, requestSchema, type FieldSpec } from './schema'
import { needsModels, SETTINGS_MODEL_FIELDS, withSwitchCounts, type SettingsSectionName } from './settingsPage'

// One section of the platform settings on the Settings page (admin design §5.10, §4.8): its fields from the request schema of
// `PUT /api/settings/{section}` (§5.8), the model fields as pickers over the gateway mirror at the platform ceiling (#466),
// the crawl section's host politeness and the personal-data flag rules as tables. A save is that section alone, with the version read; Cancel goes
// back to the saved values.
const props = defineProps<{ section: SettingsSectionName; label: string }>()
const ui = useUiStore()

interface Loaded {
  fields: FieldSpec[]
  initial: Record<string, unknown>
  models: GatewayModel[]
}
const loaded = ref<Loaded | null>(null)
const loading = ref(false)
const error = ref<unknown>(null)
const formKey = ref(0)
const url = () => `/api/settings/${props.section}`

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const modelFields = SETTINGS_MODEL_FIELDS[props.section] ?? []
    const [doc, initial, models] = await Promise.all([
      loadOpenApi(),
      http.get<Record<string, unknown>>(url()),
      needsModels(modelFields) ? api.gateway.models() : Promise.resolve([] as GatewayModel[]),
    ])
    const schema = requestSchema(doc, 'put', url()) ?? requestSchema(doc, 'put', '/api/settings/{section}')
    loaded.value = { fields: withSwitchCounts(withModelFields(fieldsOf(doc, schema), modelFields), props.section), initial, models }
    formKey.value++
  } catch (e) {
    loaded.value = null
    error.value = e
  } finally {
    loading.value = false
  }
}

function saved(): void {
  ui.notify(SETTINGS_PAGE.saved(props.label))
  void load()
}

/** Cancel: the form again from the values loaded. */
function reset(): void {
  formKey.value++
}

watch(() => props.section, load, { immediate: true })
</script>

<template>
  <!-- A page form at most 680 px wide (admin §5.12 rule 3). -->
  <div class="max-w-[680px]" data-test="settings-section-form">
    <p v-if="loading && !loaded" role="status">{{ COMMON.loading }}</p>
    <InlineError v-if="error" :message="problemMessage(error)" class="mb-4" />
    <template v-if="loaded">
      <CrawlSettingsForm
        v-if="section === 'crawl'" :key="formKey" :initial="loaded.initial" :url="url()" :submit-label="PLATFORM.form.save" @saved="saved" @cancel="reset"
      />
      <PiiSettingsForm
        v-else-if="section === 'pii'" :key="formKey" :initial="loaded.initial" :url="url()" :submit-label="PLATFORM.form.save" @saved="saved" @cancel="reset"
      />
      <GenericForm
        v-else :key="formKey" :form-id="`settings-${section}`" :fields="loaded.fields" :initial="loaded.initial" method="put" :url="url()"
        :submit-label="PLATFORM.form.save" :models="loaded.models" fold-json @saved="saved" @cancel="reset"
      />
    </template>
  </div>
</template>
