<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { Assistant, AssistantCopyResult, Department } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { ASSISTANT, ASSISTANTS, COMMON } from '@/utils/labels'

// Copy (admin design §4.5, §5.5.4; B-55): the copy's name and department; the slug follows from the name and the owner team
// is the caller's first in the department, as when creating (teams are hidden in create forms).
const props = defineProps<{ open: boolean; assistant: Assistant }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'copied', result: AssistantCopyResult): void }>()

const name = ref('')
const departmentId = ref('')
const departments = ref<Department[]>([])
const saving = ref(false)
const form = useFormErrors({ name: 'assistant-copy-name', departmentId: 'assistant-copy-department', slug: 'assistant-copy-name' })
const departmentOptions = computed(() => departments.value.map((d) => ({ value: d.id, label: d.name })))

watch(
  () => props.open,
  async (open) => {
    if (!open) return
    name.value = ASSISTANT.copyName(props.assistant.name)
    departmentId.value = props.assistant.departmentId
    form.clear()
    departments.value = await api.organisation.departments().catch(() => [])
  },
)

async function save(): Promise<void> {
  if (!form.validate({ name: !name.value.trim() && ASSISTANTS.nameRequired })) return
  saving.value = true
  try {
    emit('copied', await api.assistants.copy(props.assistant.id, { name: name.value.trim(), departmentId: departmentId.value }))
    emit('update:open', false)
  } catch (e) {
    form.applyServerError(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="ASSISTANT.copyTitle" @update:open="emit('update:open', $event)">
    <form id="assistant-copy" class="grid gap-5" novalidate @submit.prevent="save">
      <p class="text-fg-2">{{ ASSISTANT.copyLead }}</p>
      <TextField id="assistant-copy-name" v-model="name" :label="ASSISTANTS.name" :error="form.errors.name ?? form.errors.slug" autocomplete="off" />
      <SelectField
        id="assistant-copy-department" v-model="departmentId" :label="ASSISTANT.copyDepartment" :options="departmentOptions" :error="form.errors.departmentId"
      />
      <InlineError v-if="form.formError.value" :message="form.formError.value" />
    </form>
    <template #footer>
      <UiButton variant="primary" type="submit" form="assistant-copy" :loading="saving" data-test="copy-save">{{ ASSISTANT.copy }}</UiButton>
      <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
    </template>
  </UiDrawer>
</template>
