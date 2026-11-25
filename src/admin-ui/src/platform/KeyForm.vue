<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { api } from '@/api'
import type { ApiKey, AssistantSummary, CollectionSummary, Department, KeyCreated, KeyRequest } from '@/api/types'
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

// A key's form in its app's drawer (admin design §5.9, §5.9.1). A new key asks for its name; everything else is under a
// collapsed Restrictions, open when the key has any of them or one of them is refused. An edit keeps its app and team.
// Limit to assistants lists the assistants the app offers (`audienceIds`), and any the key names that it no longer offers,
// so they can be unticked; Limit to knowledge bases lists every knowledge base, since it also limits the knowledge
// routes, which do not go through an assistant. The expiry is a date: the key stops working at the end of that day.
// The pickers' rows come from the drawer, which reads them once (`useClientChoices`), not once per key.
const props = defineProps<{
  formId: string
  apiKey: ApiKey | null
  clientId: string
  audienceIds: string[]
  assistants: AssistantSummary[]
  departments: Department[]
  collections: CollectionSummary[]
}>()
const emit = defineEmits<{ (e: 'saved', response: KeyCreated | ApiKey): void; (e: 'cancel'): void }>()

const K = KEY_FORM
const key = props.apiKey
// Mounted per key (the parent keys it), so the values are read from the props once.
const values = reactive({
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
const id = (field: string) => `${props.formId}-${field}`
const RESTRICTED = ['description', 'teamId', 'scopes', 'expiresAt', 'allowedIpRanges', 'requestsPerMinute'] as const
const form = useFormErrors({
  name: id('name'), description: id('description'), teamId: id('teamId'), scopes: id('scopes'), expiresAt: id('expiresAt'),
  allowedIpRanges: id('allowedIpRanges'), requestsPerMinute: id('requestsPerMinute'),
})
const restrictionsOpen = ref(
  !!(values.description || values.teamId || values.assistantIds.length || values.collectionIds.length || values.expiresAt || values.allowedIpRanges
    || values.requestsPerMinute),
)
const saving = ref(false)

const teamOptions = computed(() => [
  { value: '', label: K.noTeam },
  ...props.departments.flatMap((d) => d.teams.filter((t) => t.isActive || t.id === values.teamId).map((t) => ({ value: t.id, label: `${d.name}: ${t.name}` }))),
])
const scopeAssistants = computed(() =>
  props.assistants.filter((a) => props.audienceIds.includes(a.id) || values.assistantIds.includes(a.id)).map((a) => ({
    ...a, offered: props.audienceIds.includes(a.id),
  })),
)

function toggle(list: string[], value: string, on: boolean): string[] {
  return on ? [...new Set([...list, value])] : list.filter((x) => x !== value)
}

/** A refused field behind the collapsed section opens it, so the error can be seen and the focus reach the field. */
function revealErrors(): void {
  if (RESTRICTED.some((f) => form.errors[f])) restrictionsOpen.value = true
}

function body(): KeyRequest | null {
  const rpm = values.requestsPerMinute.trim()
  const perMinute = rpm === '' ? null : Number(rpm)
  const ok = form.validate({
    name: !values.name.trim() && K.nameRequired,
    requestsPerMinute: perMinute !== null && !Number.isInteger(perMinute) && PLATFORM.form.invalidNumber,
  })
  revealErrors()
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
      : await api.keys.create({ ...request, clientId: props.clientId, teamId: values.teamId || null }))
  } catch (e) {
    form.applyServerError(e)
    revealErrors()
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form :id="formId" class="grid gap-5" novalidate data-test="key-form" @submit.prevent="save">
    <TextField :id="id('name')" v-model="values.name" :label="K.name" :error="form.errors.name" autocomplete="off" />
    <div v-if="key" class="flex items-center gap-3">
      <UiSwitch v-model="values.isEnabled" :labelledby="`${id('isEnabled')}-label`" />
      <span :id="`${id('isEnabled')}-label`" class="font-medium">{{ K.isEnabled }}</span>
    </div>
    <details :open="restrictionsOpen" class="rounded-card border border-border p-3" data-test="key-restrictions" @toggle="restrictionsOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="cursor-pointer font-medium">{{ K.restrictions }}</summary>
      <p class="mt-1 text-small text-fg-3">{{ K.restrictionsHint }}</p>
      <div class="mt-4 grid gap-5">
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
              v-for="a in scopeAssistants" :key="a.id" :label="a.name" :description="a.offered ? undefined : K.notOffered"
              :model-value="values.assistantIds.includes(a.id)" @update:model-value="values.assistantIds = toggle(values.assistantIds, a.id, $event)"
            />
          </div>
          <p v-if="scopeAssistants.length === 0" class="text-small text-fg-3">{{ K.noAssistants }}</p>
        </fieldset>
        <fieldset class="grid gap-1" :aria-describedby="`${id('collections')}-hint`" data-test="scope-collections">
          <legend class="mb-1 font-medium">{{ K.collections }}</legend>
          <p :id="`${id('collections')}-hint`" class="text-small text-fg-3">{{ K.collectionsHint }}</p>
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
          :error="form.errors.requestsPerMinute" inputmode="numeric" short
        />
      </div>
    </details>
    <InlineError v-if="form.formError.value" :message="form.formError.value" />
    <div class="flex gap-2">
      <UiButton variant="primary" type="submit" :loading="saving" data-test="save-key">{{ key ? K.save : K.create }}</UiButton>
      <UiButton variant="quiet" @click="emit('cancel')">{{ COMMON.cancel }}</UiButton>
    </div>
  </form>
</template>
