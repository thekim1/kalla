<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Assistant, Level } from '@/api/types'
import GatewayKeyPicker from '@/components/gateway/GatewayKeyPicker.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { ASSISTANT, ASSISTANTS, COMMON, EDIT_DRAWER, GATEWAY_KEY, LEVEL_HELP, LEVEL_LABEL } from '@/utils/labels'
import { problemLines } from '@/utils/problem'

// Edit (admin design §5.5.4, #446): the assistant's own fields, the level included, saved through PUT /api/assistants/{id}.
// A level change re-checks the published version (V3, V7, V8, V9, V36); refusals come back after Save with their affected
// objects, and a change to public with general knowledge on asks V36's confirmation and resends with it (#445).
const props = defineProps<{ open: boolean; assistant: Assistant }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved', assistant: Assistant): void }>()

const LEVELS: Level[] = ['public', 'internal', 'sensitive']
const name = ref('')
const level = ref<Level>('internal')
const description = ref('')
const contactName = ref('')
const contactEmail = ref('')
// The gateway key (B-36, V39): refusals (another department, not usable, residency, models) come back after Save.
const gatewayKeyId = ref<string | null>(null)
const saving = ref(false)
const refusals = ref<string[]>([])
const refusalBox = ref<HTMLElement | null>(null)
const form = useFormErrors({ name: 'assistant-edit-name', description: 'assistant-edit-description', contactName: 'assistant-edit-contact', contactEmail: 'assistant-edit-email' })

watch(
  () => props.open,
  (open) => {
    if (!open) return
    name.value = props.assistant.name
    level.value = props.assistant.classification
    description.value = props.assistant.description ?? ''
    contactName.value = props.assistant.contactName ?? ''
    contactEmail.value = props.assistant.contactEmail ?? ''
    gatewayKeyId.value = props.assistant.gatewayKeyId ?? null
    refusals.value = []
    form.clear()
  },
  { immediate: true },
)

function update(confirmGeneralKnowledge: boolean): Promise<Assistant> {
  return api.assistants.update(props.assistant.id, {
    name: name.value.trim(), ownerTeamId: props.assistant.ownerTeamId, classification: level.value, description: description.value.trim() || null,
    contactName: contactName.value.trim() || null, contactEmail: contactEmail.value.trim() || null, gatewayKeyId: gatewayKeyId.value,
    version: props.assistant.version, confirmGeneralKnowledge,
  })
}

async function save(): Promise<void> {
  refusals.value = []
  if (!form.validate({ name: !name.value.trim() && ASSISTANTS.nameRequired })) return
  saving.value = true
  try {
    let saved: Assistant
    try {
      saved = await update(false)
    } catch (e) {
      if (!(e instanceof ApiError) || e.problem?.code !== 'general_knowledge_public_unconfirmed') throw e
      if (!window.confirm(ASSISTANT.generalKnowledgeConfirm)) return
      saved = await update(true)
    }
    emit('saved', saved)
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
    if (form.formError.value) {
      form.formError.value = null
      refusals.value = problemLines(e)
      // The list sits under the fields: bring it into view (role=alert announces it).
      void nextTick(() => refusalBox.value?.scrollIntoView?.({ block: 'nearest' }))
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="EDIT_DRAWER.assistantTitle" @update:open="emit('update:open', $event)">
    <form id="assistant-edit" class="grid gap-5" novalidate @submit.prevent="save">
      <TextField id="assistant-edit-name" v-model="name" :label="ASSISTANTS.name" :error="form.errors.name" autocomplete="off" />
      <fieldset class="grid gap-2" aria-describedby="assistant-edit-level-hint">
        <legend class="mb-1 font-medium">{{ ASSISTANTS.level }}</legend>
        <p id="assistant-edit-level-hint" class="text-small text-fg-2">{{ EDIT_DRAWER.levelHint }}</p>
        <label v-for="l in LEVELS" :key="l" class="flex cursor-pointer gap-3 rounded-card border border-border p-3">
          <input :id="`assistant-edit-level-${l}`" v-model="level" type="radio" name="assistant-edit-level" :value="l" class="mt-1" />
          <span><span class="block font-medium">{{ LEVEL_LABEL[l] }}</span><span class="block text-small text-fg-2">{{ LEVEL_HELP[l] }}</span></span>
        </label>
      </fieldset>
      <TextAreaField id="assistant-edit-description" v-model="description" :label="ASSISTANTS.description" :hint="ASSISTANTS.descriptionHint" :error="form.errors.description" />
      <TextField id="assistant-edit-contact" v-model="contactName" :label="ASSISTANTS.contactName" :error="form.errors.contactName" />
      <TextField id="assistant-edit-email" v-model="contactEmail" :label="ASSISTANTS.contactEmail" type="email" inputmode="email" :error="form.errors.contactEmail" />
      <GatewayKeyPicker id="assistant-edit-key" v-model="gatewayKeyId" :department-id="assistant.departmentId" :hint="GATEWAY_KEY.assistantHint" />
      <div v-if="refusals.length" ref="refusalBox" role="alert" class="rounded-card border border-danger p-3 text-small text-danger" data-test="refusals">
        <p class="font-medium">{{ EDIT_DRAWER.refused }}</p>
        <ul class="ml-5 list-disc"><li v-for="r in refusals" :key="r">{{ r }}</li></ul>
      </div>
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="assistant-edit" :loading="saving" data-test="save">{{ EDIT_DRAWER.save }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>
