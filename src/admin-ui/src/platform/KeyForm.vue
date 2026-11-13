<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { api } from '@/api'
import type { ApiKey, AssistantSummary, ClientSummary, CollectionSummary, Department, KeyCreated, KeyRequest } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiSwitch from '@/components/ui/UiSwitch.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { toLocalInput } from '@/utils/format'
import { COMMON, KEY_FORM, PLATFORM } from '@/utils/labels'

// A key's form (admin design §5.9), in a client's drawer and on the Keys page. A new key belongs to `clientId`, or to
// the client picked here when that is null; an edit (`apiKey`) keeps its client and team. Scopes go as one object; empty
// lists leave the key to the client's audiences. The expiry is a date: the key stops working at the end of that day.
const props = defineProps<{ formId: string; apiKey: ApiKey | null; clientId: string | null }>()
const emit = defineEmits<{ (e: 'saved', response: KeyCreated | ApiKey): void; (e: 'cancel'): void }>()

const K = KEY_FORM
const key = props.apiKey
// Mounted per key (the parent keys it), so the values are read from the props once.
const values = reactive({
  clientId: key?.clientId ?? props.clientId ?? '',
  name: key?.name ?? '',
  description: key?.description ?? '',
  teamId: key?.teamId ?? '',
  assistantIds: [...(key?.assistantIds ?? [])],
  collectionIds: [...(key?.collectionIds ?? [])],
  expiresAt: toLocalInput(key?.expiresAt).slice(0, 10),
  allowedIpRanges: (key?.allowedIpRanges ?? []).join('\n'),
  requestsPerMinute: key?.requestsPerMinute === null || key?.requestsPerMinute === undefined ? '' : String(key.requestsPerMinute),
  isEnabled: key?.isEnabled ?? true,
})
const pickClient = !key && !props.clientId
const id = (field: string) => `${props.formId}-${field}`
const form = useFormErrors({
  clientId: id('clientId'), name: id('name'), description: id('description'), teamId: id('teamId'), scopes: id('scopes'), expiresAt: id('expiresAt'),
  allowedIpRanges: id('allowedIpRanges'), requestsPerMinute: id('requestsPerMinute'),
})
const saving = ref(false)
const clients = ref<ClientSummary[]>([])
const departments = ref<Department[]>([])
const assistants = ref<AssistantSummary[]>([])
const collections = ref<CollectionSummary[]>([])

const clientOptions = computed(() => clients.value.map((c) => ({ value: c.id, label: c.name })))
const teamOptions = computed(() => [
  { value: '', label: K.noTeam },
  ...departments.value.flatMap((d) => d.teams.filter((t) => t.isActive || t.id === values.teamId).map((t) => ({ value: t.id, label: `${d.name}: ${t.name}` }))),
])

function toggle(list: string[], value: string, on: boolean): string[] {
  return on ? [...new Set([...list, value])] : list.filter((x) => x !== value)
}

function body(): KeyRequest | null {
  const rpm = values.requestsPerMinute.trim()
  const perMinute = rpm === '' ? null : Number(rpm)
  const ok = form.validate({
    clientId: pickClient && !values.clientId && K.clientRequired,
    name: !values.name.trim() && K.nameRequired,
    requestsPerMinute: perMinute !== null && !Number.isInteger(perMinute) && PLATFORM.form.invalidNumber,
  })
  if (!ok) return null
  return {
    name: values.name.trim(),
    description: values.description.trim() || null,
    // The end of the chosen day in the browser's time zone.
    expiresAt: values.expiresAt ? new Date(`${values.expiresAt}T23:59:59`).toISOString() : null,
    scopes: { assistantIds: values.assistantIds, collectionIds: values.collectionIds },
    allowedIpRanges: values.allowedIpRanges.split('\n').map((l) => l.trim()).filter(Boolean),
    requestsPerMinute: perMinute,
    isEnabled: values.isEnabled,
  }
}

async function save(): Promise<void> {
  const request = body()
  if (!request) return
  saving.value = true
  try {
    emit('saved', key
      ? await api.keys.update(key.id, request)
      : await api.keys.create({ ...request, clientId: values.clientId, teamId: values.teamId || null }))
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  ;[departments.value, assistants.value, collections.value, clients.value] = await Promise.all([
    api.organisation.departments().catch(() => []),
    api.assistants.list({ pageSize: 200 }).then((p) => p.items).catch(() => []),
    api.collections.list({ pageSize: 200, view: 'all' }).then((p) => p.items).catch(() => []),
    pickClient ? api.clients.list().catch(() => []) : Promise.resolve([]),
  ])
})
</script>

<template>
  <form :id="formId" class="grid gap-5" novalidate data-test="key-form" @submit.prevent="save">
    <SelectField
      v-if="pickClient" :id="id('clientId')" v-model="values.clientId" :label="K.client" :options="clientOptions" :placeholder="K.chooseClient"
      :error="form.errors.clientId"
    />
    <TextField :id="id('name')" v-model="values.name" :label="K.name" :error="form.errors.name" autocomplete="off" />
    <TextAreaField :id="id('description')" v-model="values.description" :label="K.description" :error="form.errors.description" :rows="2" />
    <SelectField
      :id="id('teamId')" v-model="values.teamId" :label="K.team" :options="teamOptions" :disabled="!!key" :hint="key ? K.teamFixed : undefined"
      :error="form.errors.teamId"
    />
    <fieldset :id="id('scopes')" class="grid gap-1" :aria-describedby="`${id('scopes')}-hint`" data-test="scope-assistants">
      <legend class="mb-1 font-medium">{{ K.assistants }}</legend>
      <p :id="`${id('scopes')}-hint`" class="text-small text-fg-3">{{ K.scopeHint }}</p>
      <div class="grid max-h-56 gap-1 overflow-auto">
        <CheckField
          v-for="a in assistants" :key="a.id" :label="a.name" :model-value="values.assistantIds.includes(a.id)"
          @update:model-value="values.assistantIds = toggle(values.assistantIds, a.id, $event)"
        />
      </div>
      <p v-if="assistants.length === 0" class="text-small text-fg-3">{{ K.noAssistants }}</p>
    </fieldset>
    <fieldset class="grid gap-1" :aria-describedby="`${id('scopes')}-hint`" data-test="scope-collections">
      <legend class="mb-1 font-medium">{{ K.collections }}</legend>
      <div class="grid max-h-56 gap-1 overflow-auto">
        <CheckField
          v-for="c in collections" :key="c.id" :label="c.name" :model-value="values.collectionIds.includes(c.id)"
          @update:model-value="values.collectionIds = toggle(values.collectionIds, c.id, $event)"
        />
      </div>
      <p v-if="collections.length === 0" class="text-small text-fg-3">{{ K.noCollections }}</p>
      <p v-if="form.errors.scopes" class="text-small text-danger">{{ form.errors.scopes }}</p>
    </fieldset>
    <TextField :id="id('expiresAt')" v-model="values.expiresAt" type="date" :label="K.expiresAt" :hint="K.expiresHint" :error="form.errors.expiresAt" />
    <TextAreaField
      :id="id('allowedIpRanges')" v-model="values.allowedIpRanges" :label="K.allowedIpRanges" :hint="K.ipHint" :error="form.errors.allowedIpRanges" :rows="3" mono
    />
    <TextField
      :id="id('requestsPerMinute')" v-model="values.requestsPerMinute" :label="K.requestsPerMinute" :hint="PLATFORM.fieldHints.requestsPerMinute"
      :error="form.errors.requestsPerMinute" inputmode="numeric"
    />
    <div class="flex items-center gap-3">
      <UiSwitch v-model="values.isEnabled" :labelledby="`${id('isEnabled')}-label`" />
      <span :id="`${id('isEnabled')}-label`" class="font-medium">{{ K.isEnabled }}</span>
    </div>
    <InlineError v-if="form.formError.value" :message="form.formError.value" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" :loading="saving" data-test="save-key">{{ key ? K.save : K.create }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </form>
</template>
